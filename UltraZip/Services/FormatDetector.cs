using System.IO;
using System.Text.RegularExpressions;
using UltraZip.Models;

namespace UltraZip.Services;

/// <summary>
/// 通过扩展名 + 文件头魔数识别压缩格式，并归并分卷集（找首卷、校验缺卷）。
/// </summary>
public static class FormatDetector
{
    private static readonly byte[] Magic7z = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };
    private static readonly byte[] MagicZip = { 0x50, 0x4B, 0x03, 0x04 };
    private static readonly byte[] MagicZipEmpty = { 0x50, 0x4B, 0x05, 0x06 };
    private static readonly byte[] MagicZipSpanned = { 0x50, 0x4B, 0x07, 0x08 };
    private static readonly byte[] MagicRar4 = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };
    private static readonly byte[] MagicRar5 = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };
    private static readonly byte[] MagicGz = { 0x1F, 0x8B, 0x08 };
    private static readonly byte[] MagicXz = { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 };
    private static readonly byte[] MagicBz2 = { 0x42, 0x5A, 0x68 };
    private static readonly byte[] MagicCab = { 0x4D, 0x53, 0x43, 0x46 };
    private static readonly byte[] MagicWim = { 0x4D, 0x53, 0x57, 0x49, 0x4D, 0x00, 0x00, 0x00 };
    private static readonly byte[] MagicZstd = { 0x28, 0xB5, 0x2F, 0xFD };
    private static readonly byte[] MagicLz4 = { 0x04, 0x22, 0x4D, 0x18 };

    private static readonly string[] ArchiveExtensions =
        { ".7z", ".zip", ".rar", ".gz", ".gzip", ".bz2", ".xz", ".tar", ".iso",
          ".cab", ".wim", ".zst", ".zstd", ".lz4", ".001" };

    /// <summary>该扩展名是否属于压缩包（或分卷）格式。</summary>
    public static bool LooksLikeArchive(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".rar") || name.EndsWith(".zip") || name.EndsWith(".7z"))
            return true;
        var ext = Path.GetExtension(name);
        if (ArchiveExtensions.Contains(ext))
            return true;
        if (Regex.IsMatch(name, @"\.(r\d{2}|z\d{2})$"))
            return true;
        if (Regex.IsMatch(name, @"\.part\d+\.rar$"))
            return true;
        if (Regex.IsMatch(name, @"\.\d{3,}$"))
            return true;
        return false;
    }

    /// <summary>读取文件头，按魔数判断格式（Unknown 表示未识别）。</summary>
    public static ArchiveFormat DetectByMagic(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[8];
            int n = fs.Read(head, 0, head.Length);
            if (n < 3) return ArchiveFormat.Unknown;
            if (StartsWith(head, MagicRar5)) return ArchiveFormat.Rar;
            if (StartsWith(head, MagicRar4)) return ArchiveFormat.Rar;
            if (StartsWith(head, Magic7z)) return ArchiveFormat.SevenZip;
            if (StartsWith(head, MagicZip) || StartsWith(head, MagicZipEmpty) || StartsWith(head, MagicZipSpanned))
                return ArchiveFormat.Zip;
            if (StartsWith(head, MagicGz)) return ArchiveFormat.GZip;
            if (StartsWith(head, MagicXz)) return ArchiveFormat.Xz;
            if (StartsWith(head, MagicBz2)) return ArchiveFormat.BZip2;
            if (StartsWith(head, MagicCab)) return ArchiveFormat.Cab;
            if (StartsWith(head, MagicWim)) return ArchiveFormat.Wim;
            if (StartsWith(head, MagicZstd)) return ArchiveFormat.Zstd;
            if (StartsWith(head, MagicLz4)) return ArchiveFormat.Lz4;
            // tar 魔数在第 257 字节 "ustar"
            if (fs.Length > 265)
            {
                fs.Seek(257, SeekOrigin.Begin);
                var ustar = new byte[5];
                if (fs.Read(ustar, 0, 5) == 5 &&
                    ustar[0] == 'u' && ustar[1] == 's' && ustar[2] == 't' && ustar[3] == 'a' && ustar[4] == 'r')
                    return ArchiveFormat.Tar;
            }
            // ISO 9660 首个主卷描述符从 32768 开始，类型字节后是 CD001
            if (fs.Length >= 32774)
            {
                fs.Seek(32769, SeekOrigin.Begin);
                var iso = new byte[5];
                if (fs.Read(iso, 0, 5) == 5 &&
                    iso[0] == 'C' && iso[1] == 'D' && iso[2] == '0' && iso[3] == '0' && iso[4] == '1')
                    return ArchiveFormat.Iso;
            }
            return ArchiveFormat.Unknown;
        }
        catch
        {
            return ArchiveFormat.Unknown;
        }
    }

    public static string FormatName(ArchiveFormat f) => f switch
    {
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.Zip => "ZIP",
        ArchiveFormat.Rar => "RAR",
        ArchiveFormat.GZip => "GZip",
        ArchiveFormat.BZip2 => "BZip2",
        ArchiveFormat.Xz => "XZ",
        ArchiveFormat.Tar => "TAR",
        ArchiveFormat.Iso => "ISO",
        ArchiveFormat.Cab => "CAB",
        ArchiveFormat.Wim => "WIM",
        ArchiveFormat.Zstd => "ZSTD",
        ArchiveFormat.Lz4 => "LZ4",
        ArchiveFormat.Other => "其他",
        _ => "未知"
    };

    /// <summary>把 7z 探测出的 Type 应用到识别结果；未知类型也允许交给 7z 解压。</summary>
    public static bool ApplyProbeType(ArchiveInfo info, string typeName)
    {
        string type = typeName.Trim();
        if (type.Length == 0) return false;
        info.ProbeTypeName = type;
        string normalized = type.ToLowerInvariant();
        info.Format = normalized switch
        {
            "7z" or "pe" => ArchiveFormat.SevenZip,
            "zip" => ArchiveFormat.Zip,
            "rar" or "rar4" or "rar5" => ArchiveFormat.Rar,
            "gzip" => ArchiveFormat.GZip,
            "bzip2" => ArchiveFormat.BZip2,
            "xz" => ArchiveFormat.Xz,
            "tar" => ArchiveFormat.Tar,
            "iso" => ArchiveFormat.Iso,
            "cab" => ArchiveFormat.Cab,
            "wim" => ArchiveFormat.Wim,
            "zstd" or "zst" => ArchiveFormat.Zstd,
            "lz4" => ArchiveFormat.Lz4,
            _ => ArchiveFormat.Other
        };
        info.IsSelfExtracting = normalized == "pe" || HasPeHeader(info.OriginalPath);
        return true;
    }

    private static bool HasPeHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 完整识别：格式 + 分卷归并。返回值中 FirstVolumePath 为应传给 7z 的文件。
    /// </summary>
    public static ArchiveInfo Detect(string path)
    {
        var info = new ArchiveInfo { OriginalPath = path, FirstVolumePath = path };
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileName(path);

        // 1) name.7z.001 / name.zip.001 数字分卷
        var m = Regex.Match(name, @"^(?<base>.+)\.(?<num>\d{3,})$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var baseName = m.Groups["base"].Value;
            var first = Path.Combine(dir, baseName + ".001");
            info.IsVolumeSet = true;
            info.FirstVolumePath = File.Exists(first) ? first : path;
            CollectNumberedVolumes(info, dir, baseName);
            info.Format = GuessVolumeBaseFormat(baseName) is var f && f != ArchiveFormat.Unknown
                ? f
                : DetectByMagic(info.FirstVolumePath);
            return info;
        }

        // 2) xxx.partN.rar 分卷
        m = Regex.Match(name, @"^(?<base>.+)\.part(?<num>\d+)\.rar$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var baseName = m.Groups["base"].Value;
            var first = Path.Combine(dir, baseName + ".part1.rar");
            info.IsVolumeSet = true;
            info.Format = ArchiveFormat.Rar;
            info.FirstVolumePath = File.Exists(first) ? first : path;
            // 从 part1 起连续枚举
            int i = 1;
            while (true)
            {
                var p = Path.Combine(dir, $"{baseName}.part{i}.rar");
                if (File.Exists(p)) { info.Volumes.Add(p); i++; }
                else
                {
                    // 若后面还有更大的卷号存在，说明中间缺卷
                    if (Directory.EnumerateFiles(dir, $"{baseName}.part*.rar")
                            .Any(f => ParsePartNumber(f) > i))
                        info.MissingVolumes.Add(p);
                    break;
                }
            }
            return info;
        }

        // 3) name.r00 / name.r01 ...（RAR 老式分卷，首卷为 .rar）
        m = Regex.Match(name, @"^(?<base>.+)\.r(?<num>\d{2})$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var baseName = m.Groups["base"].Value;
            var first = Path.Combine(dir, baseName + ".rar");
            info.IsVolumeSet = true;
            info.Format = ArchiveFormat.Rar;
            info.FirstVolumePath = File.Exists(first) ? first : path;
            CollectSuffixedVolumes(info, dir, baseName, ".rar", ".r");
            return info;
        }

        // 4) name.z01 / name.z02 ...（ZIP 分卷，末卷为 .zip，首传给 7z 用 .zip）
        m = Regex.Match(name, @"^(?<base>.+)\.z(?<num>\d{2})$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var baseName = m.Groups["base"].Value;
            var first = Path.Combine(dir, baseName + ".zip");
            info.IsVolumeSet = true;
            info.Format = ArchiveFormat.Zip;
            info.FirstVolumePath = File.Exists(first) ? first : path;
            CollectSuffixedVolumes(info, dir, baseName, ".zip", ".z");
            return info;
        }

        // 5) 拖入老式 RAR 首卷或 ZIP 末卷时，也要归并同目录的其他分卷
        var extension = Path.GetExtension(name);
        if (extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var baseName = Path.GetFileNameWithoutExtension(name);
            string prefix = extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) ? ".r" : ".z";
            bool hasSuffixedVolumes = Directory.EnumerateFiles(dir, baseName + ".*")
                .Any(f => Regex.IsMatch(Path.GetFileName(f),
                    Regex.Escape(baseName) + Regex.Escape(prefix) + @"\d{2}$",
                    RegexOptions.IgnoreCase));
            if (hasSuffixedVolumes)
            {
                info.IsVolumeSet = true;
                info.Format = extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
                    ? ArchiveFormat.Rar
                    : ArchiveFormat.Zip;
                CollectSuffixedVolumes(info, dir, baseName, extension, prefix);
                return info;
            }
        }

        // 6) 单文件：魔数 → EOCD/ZIP64（含前置数据）→ 扩展名；7z 探测由 UI 异步兜底
        info.Format = DetectByMagic(path);
        if (info.Format == ArchiveFormat.Unknown)
        {
            var prefixSize = ZipAesEngine.GetPrefixSize(path);
            if (prefixSize.HasValue)
            {
                info.Format = ArchiveFormat.Zip;
                info.PrefixSize = prefixSize.Value;
                info.ProbeTypeName = "ZIP";
                return info;
            }
            info.Format = FormatFromExtension(name);
        }
        return info;
    }

    private static int ParsePartNumber(string file)
    {
        var mm = Regex.Match(Path.GetFileName(file), @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        return mm.Success && int.TryParse(mm.Groups[1].Value, out var v) ? v : 0;
    }

    /// <summary>name.001, name.002 ... 连续枚举；若后面存在跳号卷则记录缺卷。</summary>
    private static void CollectNumberedVolumes(ArchiveInfo info, string dir, string baseName)
    {
        int i = 1;
        while (true)
        {
            var p = Path.Combine(dir, $"{baseName}.{i:000}");
            if (File.Exists(p)) { info.Volumes.Add(p); i++; }
            else
            {
                bool hasLater = Directory.EnumerateFiles(dir, baseName + ".*")
                    .Select(f => Regex.Match(Path.GetFileName(f), @"\.(\d{3,})$"))
                    .Where(mm => mm.Success)
                    .Any(mm => int.Parse(mm.Groups[1].Value) > i);
                if (hasLater) info.MissingVolumes.Add(p);
                break;
            }
        }
    }

    /// <summary>.rar+.r00... 或 .zip+.z01... 归并，并列出缺失的首/末卷及中间卷。</summary>
    private static void CollectSuffixedVolumes(ArchiveInfo info, string dir, string baseName,
        string anchorExt, string prefix)
    {
        var anchor = Path.Combine(dir, baseName + anchorExt);
        if (File.Exists(anchor)) info.Volumes.Add(anchor);
        else info.MissingVolumes.Add(anchor);

        var suffixVolumes = Directory.EnumerateFiles(dir, baseName + ".*")
            .Select(f => new
            {
                Path = f,
                Match = Regex.Match(Path.GetFileName(f),
                    Regex.Escape(prefix) + @"(\d{2})$", RegexOptions.IgnoreCase)
            })
            .Where(x => x.Match.Success)
            .Select(x => new { x.Path, Number = int.Parse(x.Match.Groups[1].Value) })
            .OrderBy(x => x.Number)
            .ToList();
        if (suffixVolumes.Count == 0) return;

        int start = anchorExt.Equals(".zip", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        int end = suffixVolumes[^1].Number;
        for (int i = start; i <= end; i++)
        {
            var p = Path.Combine(dir, $"{baseName}{prefix}{i:00}");
            if (File.Exists(p)) info.Volumes.Add(p);
            else info.MissingVolumes.Add(p);
        }
    }

    private static ArchiveFormat GuessVolumeBaseFormat(string baseName)
    {
        var ext = Path.GetExtension(baseName).ToLowerInvariant();
        return ext switch
        {
            ".7z" => ArchiveFormat.SevenZip,
            ".zip" => ArchiveFormat.Zip,
            ".rar" => ArchiveFormat.Rar,
            ".gz" => ArchiveFormat.GZip,
            ".bz2" => ArchiveFormat.BZip2,
            ".xz" => ArchiveFormat.Xz,
            ".tar" => ArchiveFormat.Tar,
            ".iso" => ArchiveFormat.Iso,
            ".cab" => ArchiveFormat.Cab,
            ".wim" => ArchiveFormat.Wim,
            ".zst" or ".zstd" => ArchiveFormat.Zstd,
            ".lz4" => ArchiveFormat.Lz4,
            _ => ArchiveFormat.Unknown
        };
    }

    private static ArchiveFormat FormatFromExtension(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".7z")) return ArchiveFormat.SevenZip;
        if (lower.EndsWith(".zip")) return ArchiveFormat.Zip;
        if (lower.EndsWith(".rar")) return ArchiveFormat.Rar;
        if (lower.EndsWith(".gz") || lower.EndsWith(".gzip")) return ArchiveFormat.GZip;
        if (lower.EndsWith(".bz2")) return ArchiveFormat.BZip2;
        if (lower.EndsWith(".xz")) return ArchiveFormat.Xz;
        if (lower.EndsWith(".tar")) return ArchiveFormat.Tar;
        if (lower.EndsWith(".iso")) return ArchiveFormat.Iso;
        if (lower.EndsWith(".cab")) return ArchiveFormat.Cab;
        if (lower.EndsWith(".wim")) return ArchiveFormat.Wim;
        if (lower.EndsWith(".zst") || lower.EndsWith(".zstd")) return ArchiveFormat.Zstd;
        if (lower.EndsWith(".lz4")) return ArchiveFormat.Lz4;
        return ArchiveFormat.Unknown;
    }

    private static bool StartsWith(byte[] data, byte[] magic)
    {
        if (data.Length < magic.Length) return false;
        for (int i = 0; i < magic.Length; i++)
            if (data[i] != magic[i]) return false;
        return true;
    }
}
