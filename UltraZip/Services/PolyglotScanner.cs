using System.IO;
using UltraZip.Models;

namespace UltraZip.Services;

/// <summary>
/// 伪装文件（polyglot）扫描器：分块读取全文件（块间重叠 64 字节），
/// 查找压缩包魔数偏移；ZIP 额外记录最后一个 EOCD（PK\x05\x06）用于估算与校验。
/// </summary>
public static class PolyglotScanner
{
    private const int ChunkSize = 1024 * 1024;
    private const int Overlap = 64;

    private record Signature(byte[] Magic, string Format);

    private static readonly Signature[] Signatures =
    {
        new(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "ZIP"),
        new(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, "7z"),
        new(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, "RAR4"),
        new(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, "RAR5"),
        new(new byte[] { 0x1F, 0x8B, 0x08 }, "GZip"),
    };

    private static readonly byte[] EocdMagic = { 0x50, 0x4B, 0x05, 0x06 };
    private static readonly byte[] CdEntryMagic = { 0x50, 0x4B, 0x01, 0x02 };

    /// <summary>
    /// 扫描文件，返回按偏移升序的内嵌压缩包列表。
    /// skipOffsetZero=true 时忽略文件头处的匹配（主格式已由 FormatDetector 处理）。
    /// </summary>
    public static async Task<List<EmbeddedArchive>> ScanAsync(string path,
        IProgress<int>? percentProgress = null, CancellationToken ct = default)
    {
        return await Task.Run(() => Scan(path, percentProgress, ct), ct);
    }

    public static List<EmbeddedArchive> Scan(string path,
        IProgress<int>? percentProgress = null, CancellationToken ct = default)
    {
        var found = new Dictionary<string, SortedSet<long>>(StringComparer.Ordinal);
        foreach (var sig in Signatures)
            found[sig.Format] = new SortedSet<long>();

        long lastEocd = -1;
        var cdEntryOffsets = new List<long>();

        var fileLen = new FileInfo(path).Length;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[ChunkSize + Overlap];
        long position = 0;
        long scannedBase = 0; // buffer[0] 对应的文件偏移

        while (position < fileLen)
        {
            ct.ThrowIfCancellationRequested();

            // 保留上一块尾部 Overlap 字节到 buffer 头部，防止跨块漏检
            int carry = 0;
            if (position > 0)
            {
                carry = Overlap;
                // buffer 前 Overlap 字节已是上一块尾部（上一轮填充）
            }
            fs.Seek(position, SeekOrigin.Begin);
            int read = 0;
            while (read < ChunkSize && position + read < fileLen)
            {
                int n = fs.Read(buffer, carry + read, ChunkSize - read);
                if (n == 0) break;
                read += n;
            }
            int valid = carry + read;
            if (valid == 0) break;

            ScanBuffer(buffer, scannedBase + (carry > 0 ? 0 : 0), valid, carry, found,
                ref lastEocd, cdEntryOffsets);

            position += read;
            scannedBase = position - carry;
            if (carry > 0 && read > 0)
            {
                // 把本轮尾部 Overlap 字节复制到 buffer 头部供下轮使用
                Buffer.BlockCopy(buffer, valid - Overlap, buffer, 0, Overlap);
            }

            percentProgress?.Report((int)Math.Min(100, position * 100 / Math.Max(1, fileLen)));
        }

        var results = new List<EmbeddedArchive>();
        foreach (var sig in Signatures)
        {
            foreach (var off in found[sig.Format])
            {
                // 偏移 0 处即文件本身格式，不算"内嵌"
                if (off == 0) continue;
                var item = new EmbeddedArchive { Format = sig.Format, Offset = off };
                if (sig.Format == "ZIP")
                {
                    int entries = cdEntryOffsets.Count(o => o > off);
                    var notes = new List<string>();
                    if (entries > 0) notes.Add($"约 {entries} 个条目");
                    if (lastEocd > off) notes.Add($"EOCD 位于 0x{lastEocd:X}");
                    item.Note = string.Join("，", notes);
                }
                else if (sig.Format == "GZip")
                {
                    item.Note = "gzip 单数据流";
                }
                results.Add(item);
            }
        }
        return results.OrderBy(r => r.Offset).ToList();
    }

    private static void ScanBuffer(byte[] buffer, long baseOffset, int valid, int skipHead,
        Dictionary<string, SortedSet<long>> found, ref long lastEocd, List<long> cdEntryOffsets)
    {
        // 重叠区照常扫描，靠 SortedSet / 重复判断去重；Match 内部做边界检查。
        for (int i = 0; i < valid; i++)
        {
            byte b = buffer[i];
            if (b == EocdMagic[0] && Match(buffer, i, valid, EocdMagic))
                lastEocd = Math.Max(lastEocd, baseOffset + i);
            if (b == CdEntryMagic[0] && Match(buffer, i, valid, CdEntryMagic))
            {
                long off = baseOffset + i;
                if (cdEntryOffsets.Count == 0 || cdEntryOffsets[^1] != off)
                    cdEntryOffsets.Add(off);
            }
            foreach (var sig in Signatures)
            {
                if (b == sig.Magic[0] && Match(buffer, i, valid, sig.Magic))
                    found[sig.Format].Add(baseOffset + i);
            }
        }
        _ = skipHead;
    }

    private static bool Match(byte[] buffer, int pos, int valid, byte[] magic)
    {
        if (pos + magic.Length > valid) return false;
        for (int j = 0; j < magic.Length; j++)
            if (buffer[pos + j] != magic[j]) return false;
        return true;
    }
}
