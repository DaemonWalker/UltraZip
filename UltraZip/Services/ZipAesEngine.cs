using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UltraZip.Models;

namespace UltraZip.Services;

public static class ZipAesEngine
{
    private const uint LocalHeaderSignature = 0x04034B50;
    private const uint CentralHeaderSignature = 0x02014B50;
    private const uint EndSignature = 0x06054B50;
    private const uint Zip64EndSignature = 0x06064B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const int BufferSize = 128 * 1024;

    private static readonly Encoding[] PasswordEncodings = CreatePasswordEncodings();

    static ZipAesEngine()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static long? GetPrefixSize(string archivePath) => TryReadLayout(archivePath)?.PrefixSize;

    public static List<ArchiveEntry>? ListEntries(string archivePath)
    {
        var layout = TryReadLayout(archivePath);
        if (layout == null || !NeedsCustomExtractor(layout)) return null;
        return layout.Entries.Select(e => new ArchiveEntry
        {
            Path = e.Name,
            Size = e.UncompressedSize,
            PackedSize = e.CompressedSize,
            Crc = e.HasRealCrc ? e.Crc32.ToString("X8") : "",
            Method = e.Method == 99 ? "WinZip-AES" : e.RealMethod == 0 ? "Store" : "Deflate",
            IsDirectory = e.IsDirectory
        }).ToList();
    }

    public static ArchiveSummary? GetSummary(string archivePath)
    {
        var layout = TryReadLayout(archivePath);
        if (layout == null || !NeedsCustomExtractor(layout)) return null;
        return new ArchiveSummary
        {
            EntryCount = layout.Entries.Count,
            IsEncrypted = layout.Entries.Any(e => e.IsEncrypted),
            TypeName = layout.PrefixSize > 0 ? "ZIP（含前置数据）" : "ZIP"
        };
    }

    public static async Task<ExtractResult?> ExtractAsync(string archivePath, string destDir,
        string? password, IProgress<ExtractProgress>? progress, CancellationToken ct,
        IReadOnlyCollection<string>? selectedPaths = null)
    {
        var layout = TryReadLayout(archivePath);
        if (layout == null || !NeedsCustomExtractor(layout)) return null;

        try
        {
            return await Task.Run(() => Extract(layout, destDir, password, progress, ct, selectedPaths), ct);
        }
        catch (OperationCanceledException)
        {
            return ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
        }
        catch (InvalidDataException ex)
        {
            return ExtractResult.Fail(ExtractErrorKind.Corrupt, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ExtractResult.Fail(ExtractErrorKind.Other, ex.Message);
        }
    }

    private static ExtractResult Extract(ZipLayout layout, string destDir, string? password,
        IProgress<ExtractProgress>? progress, CancellationToken ct,
        IReadOnlyCollection<string>? selectedPaths)
    {
        var entries = SelectEntries(layout.Entries, selectedPaths);
        bool encrypted = entries.Any(e => e.IsEncrypted && !e.IsDirectory);
        if (encrypted && string.IsNullOrEmpty(password))
            return ExtractResult.Fail(ExtractErrorKind.NeedsPassword, "该 ZIP 已加密，需要密码。");
        if (entries.Any(e => e.IsEncrypted && e.Method != 99 && !e.IsDirectory))
            return ExtractResult.Fail(ExtractErrorKind.Other, "该拼接 ZIP 使用了暂不支持的传统 ZipCrypto 加密。");
        if (entries.Any(e => e.RealMethod is not (0 or 8) && !e.IsDirectory))
            return ExtractResult.Fail(ExtractErrorKind.Other, "ZIP 中包含暂不支持的数据压缩方式，仅支持 Store 和 Deflate。");

        Directory.CreateDirectory(destDir);
        string destRoot = Path.GetFullPath(destDir);
        long totalBytes = entries.Sum(e => (long)e.CompressedSize);
        long completedBytes = 0;

        using var fs = new FileStream(layout.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            ReportProgress(progress, completedBytes, totalBytes, entry.Name);
            string outputPath = GetSafeOutputPath(destRoot, entry.Name);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            string? parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            string tempPath = outputPath + $".ultrazip-{Guid.NewGuid():N}.tmp";
            try
            {
                long dataOffset = GetDataOffset(fs, entry);
                ValidateDataRange(fs.Length, layout.ContentEnd, entry, dataOffset);
                AesKeys? keys = null;
                if (entry.IsEncrypted)
                {
                    if (!TryDeriveKeys(fs, entry, dataOffset, password!, out keys))
                        return ExtractResult.Fail(ExtractErrorKind.WrongPassword,
                            $"密码与条目“{entry.Name}”不匹配；已自动尝试 UTF-8、GBK 等密码编码。");
                }

                ExtractEntryData(fs, entry, dataOffset, keys, tempPath, ct);
                File.Move(tempPath, outputPath, true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }

            completedBytes += (long)entry.CompressedSize;
            ReportProgress(progress, completedBytes, totalBytes, entry.Name);
        }

        return ExtractResult.Ok(password);
    }

    private static List<ZipEntryInfo> SelectEntries(List<ZipEntryInfo> entries,
        IReadOnlyCollection<string>? selectedPaths)
    {
        if (selectedPaths == null) return entries;
        var selected = selectedPaths.Select(p => p.TrimEnd('/', '\\'))
            .Where(p => p.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return entries.Where(e => selected.Contains(e.Name) || selected.Any(s =>
            e.Name.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase) ||
            e.Name.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static void ExtractEntryData(FileStream fs, ZipEntryInfo entry, long dataOffset,
        AesKeys? keys, string tempPath, CancellationToken ct)
    {
        byte[]? expectedAuth = null;
        long cipherLength = (long)entry.CompressedSize;
        long inputOffset = dataOffset;
        if (entry.IsEncrypted)
        {
            var aes = entry.Aes ?? throw new InvalidDataException("ZIP 条目缺少 WinZip-AES 参数。");
            int saltLength = GetSaltLength(aes.Strength);
            cipherLength -= saltLength + 12;
            inputOffset += saltLength + 2;
            expectedAuth = ReadAt(fs, dataOffset + (long)entry.CompressedSize - 10, 10);
        }

        fs.Seek(inputOffset, SeekOrigin.Begin);
        using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var crc = new Crc32();
        long written;
        if (entry.IsEncrypted)
        {
            using var input = new AesDecryptionStream(fs, cipherLength, keys!);
            written = Decompress(input, entry.RealMethod, entry.UncompressedSize, output, crc, ct);
            if (input.Position != cipherLength)
                throw new InvalidDataException($"条目“{entry.Name}”的加密数据未完整读取。");
            byte[] actualAuth = input.AuthenticationCode;
            if (!CryptographicOperations.FixedTimeEquals(expectedAuth, actualAuth))
                throw new InvalidDataException($"条目“{entry.Name}”的 HMAC 校验失败，数据可能已损坏。" +
                    $"（存储 {Convert.ToHexString(expectedAuth!)}，计算 {Convert.ToHexString(actualAuth)}）");
        }
        else
        {
            using var input = new BoundedReadStream(fs, cipherLength);
            written = Decompress(input, entry.RealMethod, entry.UncompressedSize, output, crc, ct);
        }

        if (written != (long)entry.UncompressedSize)
            throw new InvalidDataException($"条目“{entry.Name}”解压后的大小不匹配。");
        if (entry.HasRealCrc && crc.Value != entry.Crc32)
            throw new InvalidDataException($"条目“{entry.Name}”的 CRC 校验失败，数据可能已损坏。");
    }

    private static long Decompress(Stream input, ushort method, ulong expectedSize,
        Stream output, Crc32 crc, CancellationToken ct)
    {
        if (method == 0) return CopyAndHash(input, output, crc, ct);
        if (method != 8) throw new NotSupportedException("仅支持 Store 和 Deflate 压缩方式。");
        using var deflate = new DeflateStream(input, CompressionMode.Decompress, true);
        return CopyAndHash(deflate, output, crc, ct);
    }

    private static long CopyAndHash(Stream input, Stream output, Crc32 crc, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            crc.Append(buffer, read);
            total += read;
        }
        return total;
    }

    private static bool TryDeriveKeys(FileStream fs, ZipEntryInfo entry, long dataOffset,
        string password, out AesKeys? keys)
    {
        keys = null;
        var aes = entry.Aes ?? throw new InvalidDataException("ZIP 条目缺少 WinZip-AES 参数。");
        int saltLength = GetSaltLength(aes.Strength);
        byte[] salt = ReadAt(fs, dataOffset, saltLength);
        byte[] storedVerifier = ReadAt(fs, dataOffset + saltLength, 2);
        foreach (var encoding in PasswordEncodings)
        {
            byte[] passwordBytes;
            try { passwordBytes = encoding.GetBytes(password); }
            catch (EncoderFallbackException) { continue; }
            var candidate = AesKeys.Derive(passwordBytes, salt, aes.Strength);
            if (!CryptographicOperations.FixedTimeEquals(storedVerifier, candidate.Verifier)) continue;
            keys = candidate;
            return true;
        }
        return false;
    }

    private static long GetDataOffset(FileStream fs, ZipEntryInfo entry)
    {
        byte[] header = ReadAt(fs, entry.LocalHeaderOffset, 30);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalHeaderSignature)
            throw new InvalidDataException($"找不到条目“{entry.Name}”的本地文件头。");
        ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        return checked(entry.LocalHeaderOffset + 30L + nameLength + extraLength);
    }

    private static void ValidateDataRange(long fileLength, long contentEnd, ZipEntryInfo entry, long dataOffset)
    {
        long dataEnd = checked(dataOffset + (long)entry.CompressedSize);
        if (dataOffset < 0 || dataEnd > fileLength || dataEnd > contentEnd)
            throw new InvalidDataException($"条目“{entry.Name}”的数据范围超出 ZIP 内容。");
        if (entry.IsEncrypted)
        {
            int overhead = GetSaltLength(entry.Aes!.Strength) + 12;
            if (entry.CompressedSize < (ulong)overhead)
                throw new InvalidDataException($"条目“{entry.Name}”的 AES 数据长度无效。");
        }
    }

    private static string GetSafeOutputPath(string destRoot, string entryName)
    {
        string relative = entryName.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException($"ZIP 条目使用了不安全的绝对路径：{entryName}");
        string fullPath = Path.GetFullPath(Path.Combine(destRoot, relative));
        string rootWithSeparator = destRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.Equals(destRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"ZIP 条目试图写到目标目录之外：{entryName}");
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"ZIP 条目包含 Windows 无效文件名：{entryName}");
        }
        return fullPath;
    }

    private static void ReportProgress(IProgress<ExtractProgress>? progress, long completed,
        long total, string currentFile)
    {
        int percent = total <= 0 ? 100 : (int)Math.Clamp(completed * 100 / total, 0, 100);
        progress?.Report(new ExtractProgress { Percent = percent, CurrentFile = currentFile });
    }

    private static bool NeedsCustomExtractor(ZipLayout layout) =>
        layout.PrefixSize != 0 || layout.Entries.Any(e => e.Method == 99);

    private static ZipLayout? TryReadLayout(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 22) return null;
            int tailLength = (int)Math.Min(fs.Length, 65557);
            byte[] tail = ReadAt(fs, fs.Length - tailLength, tailLength);
            for (int pos = tailLength - 22; pos >= 0; pos--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(pos)) != EndSignature) continue;
                ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(pos + 20));
                if (pos + 22L + commentLength > tailLength) continue;
                long eocdOffset = fs.Length - tailLength + pos;
                try { return ReadLayout(fs, path, tail.AsSpan(pos, 22), eocdOffset); }
                catch (InvalidDataException) { }
                catch (ArgumentOutOfRangeException) { }
                catch (OverflowException) { }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ZipLayout ReadLayout(FileStream fs, string path, ReadOnlySpan<byte> eocd,
        long eocdOffset)
    {
        ushort disk = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(4));
        ushort cdDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(6));
        ulong entryCount = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(10));
        ulong cdSize = BinaryPrimitives.ReadUInt32LittleEndian(eocd.Slice(12));
        ulong cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd.Slice(16));
        if (disk != 0 || cdDisk != 0)
            throw new InvalidDataException("不支持分卷 ZIP。");

        long directoryEnd = eocdOffset;
        if (eocdOffset >= 20)
        {
            byte[] locator = ReadAt(fs, eocdOffset - 20, 20);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == Zip64LocatorSignature)
            {
                long zip64Offset = eocdOffset - 20 - 56;
                byte[] zip64 = ReadAt(fs, zip64Offset, 56);
                if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EndSignature)
                    throw new InvalidDataException("ZIP64 目录记录无效。");
                entryCount = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
                cdSize = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
                cdOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
                directoryEnd = zip64Offset;
            }
        }

        long prefix = checked((long)(directoryEnd - (long)cdOffset - (long)cdSize));
        if (prefix < 0 || cdOffset > long.MaxValue || cdSize > long.MaxValue || entryCount > int.MaxValue)
            throw new InvalidDataException("ZIP 中央目录偏移无效。");
        long realCdOffset = checked((long)cdOffset + prefix);
        if (realCdOffset < 0 || realCdOffset + (long)cdSize > directoryEnd)
            throw new InvalidDataException("ZIP 中央目录范围无效。");

        var entries = ReadEntries(fs, realCdOffset, directoryEnd, (int)entryCount, prefix);
        return new ZipLayout(path, prefix, directoryEnd, entries);
    }

    private static List<ZipEntryInfo> ReadEntries(FileStream fs, long cdOffset, long cdEnd,
        int entryCount, long prefix)
    {
        var entries = new List<ZipEntryInfo>(entryCount);
        fs.Seek(cdOffset, SeekOrigin.Begin);
        for (int i = 0; i < entryCount; i++)
        {
            byte[] fixedPart = ReadExact(fs, 46);
            if (BinaryPrimitives.ReadUInt32LittleEndian(fixedPart) != CentralHeaderSignature)
                throw new InvalidDataException("ZIP 中央目录签名无效。");
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(8));
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(10));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(16));
            ulong compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(20));
            ulong uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(24));
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(28));
            ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(30));
            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(32));
            ulong localOffset = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(42));
            byte[] nameBytes = ReadExact(fs, nameLength);
            byte[] extra = ReadExact(fs, extraLength);
            _ = ReadExact(fs, commentLength);

            ReadZip64Sizes(extra, ref uncompressedSize, ref compressedSize, ref localOffset);
            var aes = ReadAesExtra(extra);
            if (method == 99 && aes == null)
                throw new InvalidDataException("ZIP AES 条目缺少 0x9901 扩展字段。");
            string name = DecodeEntryName(nameBytes, extra, flags);
            entries.Add(new ZipEntryInfo
            {
                Name = name,
                Flags = flags,
                Method = method,
                RealMethod = aes?.RealMethod ?? method,
                Crc32 = crc,
                HasRealCrc = method != 99 || aes!.Version == 1,
                CompressedSize = compressedSize,
                UncompressedSize = uncompressedSize,
                LocalHeaderOffset = checked((long)localOffset + prefix),
                Aes = aes
            });
            if (fs.Position > cdEnd) throw new InvalidDataException("ZIP 中央目录条目越界。");
        }
        return entries;
    }

    private static void ReadZip64Sizes(byte[] extra, ref ulong uncompressedSize,
        ref ulong compressedSize, ref ulong localOffset)
    {
        foreach (var field in EnumerateExtraFields(extra))
        {
            if (field.Id != 0x0001) continue;
            var data = field.Data;
            int offset = 0;
            if (uncompressedSize == uint.MaxValue)
            {
                uncompressedSize = ReadUInt64(data, ref offset);
                offset += 8;
            }
            if (compressedSize == uint.MaxValue)
            {
                compressedSize = ReadUInt64(data, ref offset);
                offset += 8;
            }
            if (localOffset == uint.MaxValue)
                localOffset = ReadUInt64(data, ref offset);
            return;
        }
        if (uncompressedSize == uint.MaxValue || compressedSize == uint.MaxValue || localOffset == uint.MaxValue)
            throw new InvalidDataException("ZIP64 条目缺少大小或偏移字段。");
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 8 > data.Length) throw new InvalidDataException("ZIP64 扩展字段不完整。");
        return BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
    }

    private static AesExtra? ReadAesExtra(byte[] extra)
    {
        foreach (var field in EnumerateExtraFields(extra))
        {
            if (field.Id != 0x9901) continue;
            if (field.Data.Length != 7) throw new InvalidDataException("WinZip-AES 扩展字段长度无效。");
            ushort vendor = BinaryPrimitives.ReadUInt16LittleEndian(field.Data.AsSpan(2));
            if (vendor != 0x4541) throw new InvalidDataException("WinZip-AES 厂商标识无效。");
            byte strength = field.Data[4];
            _ = GetSaltLength(strength);
            return new AesExtra(
                BinaryPrimitives.ReadUInt16LittleEndian(field.Data),
                strength,
                BinaryPrimitives.ReadUInt16LittleEndian(field.Data.AsSpan(5)));
        }
        return null;
    }

    private static IEnumerable<(ushort Id, byte[] Data)> EnumerateExtraFields(byte[] extra)
    {
        int offset = 0;
        while (offset < extra.Length)
        {
            if (offset + 4 > extra.Length) throw new InvalidDataException("ZIP 扩展字段不完整。");
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(offset));
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(offset + 2));
            offset += 4;
            if (offset + length > extra.Length) throw new InvalidDataException("ZIP 扩展字段越界。");
            yield return (id, extra.AsSpan(offset, length).ToArray());
            offset += length;
        }
    }

    private static string DecodeEntryName(byte[] nameBytes, byte[] extra, ushort flags)
    {
        foreach (var field in EnumerateExtraFields(extra))
        {
            if (field.Id != 0x7075 || field.Data.Length < 5 || field.Data[0] != 1) continue;
            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(field.Data.AsSpan(1));
            var crc = new Crc32();
            crc.Append(nameBytes, nameBytes.Length);
            if (crc.Value == expectedCrc) return Encoding.UTF8.GetString(field.Data.AsSpan(5));
        }
        Encoding encoding = (flags & 0x0800) != 0 ? Encoding.UTF8 : Encoding.GetEncoding(936);
        return encoding.GetString(nameBytes);
    }

    private static int GetSaltLength(byte strength) => strength switch
    {
        1 => 8,
        2 => 12,
        3 => 16,
        _ => throw new InvalidDataException("未知的 WinZip-AES 强度。")
    };

    private static byte[] ReadAt(FileStream fs, long offset, int count)
    {
        if (offset < 0 || offset + count > fs.Length) throw new InvalidDataException("ZIP 数据范围越界。");
        fs.Seek(offset, SeekOrigin.Begin);
        return ReadExact(fs, count);
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        byte[] data = new byte[count];
        stream.ReadExactly(data, 0, count);
        return data;
    }

    private static Encoding[] CreatePasswordEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new[]
        {
            new UTF8Encoding(false, true),
            Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            new UnicodeEncoding(false, false, true),
            new UnicodeEncoding(true, false, true),
            new UTF8Encoding(true, true),
            Encoding.GetEncoding(437, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        };
    }

    private sealed record ZipLayout(string Path, long PrefixSize, long ContentEnd, List<ZipEntryInfo> Entries);

    private sealed record AesExtra(ushort Version, byte Strength, ushort RealMethod);

    private sealed class ZipEntryInfo
    {
        public string Name { get; init; } = "";
        public ushort Flags { get; init; }
        public ushort Method { get; init; }
        public ushort RealMethod { get; init; }
        public uint Crc32 { get; init; }
        public bool HasRealCrc { get; init; }
        public ulong CompressedSize { get; init; }
        public ulong UncompressedSize { get; init; }
        public long LocalHeaderOffset { get; init; }
        public AesExtra? Aes { get; init; }
        public bool IsEncrypted => (Flags & 1) != 0;
        public bool IsDirectory => Name.EndsWith('/') || Name.EndsWith('\\');
    }

    private sealed class AesKeys
    {
        public byte[] Encryption { get; private init; } = Array.Empty<byte>();
        public byte[] Mac { get; private init; } = Array.Empty<byte>();
        public byte[] Verifier { get; private init; } = Array.Empty<byte>();

        public static AesKeys Derive(byte[] password, byte[] salt, byte strength)
        {
            int keyLength = strength switch { 1 => 16, 2 => 24, 3 => 32, _ => 0 };
            byte[] derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1000,
                HashAlgorithmName.SHA1, keyLength * 2 + 2);
            return new AesKeys
            {
                Encryption = derived[..keyLength],
                Mac = derived[keyLength..(keyLength * 2)],
                Verifier = derived[(keyLength * 2)..]
            };
        }
    }

    private sealed class Crc32
    {
        private static readonly uint[] Table = CreateTable();
        private uint _crc = uint.MaxValue;
        public uint Value => ~_crc;

        public void Append(byte[] data, int length)
        {
            for (int i = 0; i < length; i++)
                _crc = Table[(_crc ^ data[i]) & 0xFF] ^ (_crc >> 8);
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int j = 0; j < 8; j++)
                    value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }
    }

    private class BoundedReadStream : Stream
    {
        protected readonly Stream Inner;
        private readonly long _length;
        private long _remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            Inner = inner;
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining == 0) return 0;
            int read = Inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }
    }

    private sealed class AesDecryptionStream : BoundedReadStream
    {
        private readonly Aes _aes = Aes.Create();
        private readonly ICryptoTransform _encryptor;
        private readonly HMACSHA1 _hmac = new();
        private readonly byte[] _cipher = new byte[BufferSize];
        private readonly byte[] _counters = new byte[BufferSize];
        private readonly byte[] _keyStream = new byte[BufferSize];
        private ulong _counter = 1;

        public AesDecryptionStream(Stream inner, long length, AesKeys keys) : base(inner, length)
        {
            _hmac.Key = keys.Mac;
            _aes.Key = keys.Encryption;
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _encryptor = _aes.CreateEncryptor();
        }

        public byte[] AuthenticationCode
        {
            get
            {
                _hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return _hmac.Hash![..10];
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(_cipher, 0, Math.Min(count, _cipher.Length));
            if (read == 0) return 0;
            _hmac.TransformBlock(_cipher, 0, read, null, 0);
            int blockCount = (read + 15) / 16;
            int keyStreamLength = blockCount * 16;
            Array.Clear(_counters, 0, keyStreamLength);
            for (int i = 0; i < blockCount; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(_counters.AsSpan(i * 16), _counter + (ulong)i);
            _encryptor.TransformBlock(_counters, 0, keyStreamLength, _keyStream, 0);
            for (int i = 0; i < read; i++) buffer[offset + i] = (byte)(_cipher[i] ^ _keyStream[i]);
            _counter += (ulong)blockCount;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _encryptor.Dispose();
                _aes.Dispose();
                _hmac.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
