namespace UltraZip.Models;

/// <summary>密码库条目（按用户要求明文保存）。</summary>
public class PasswordEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public string Password { get; set; } = "";
    public int HitCount { get; set; }
    public DateTime? LastUsed { get; set; }
}

/// <summary>扫描到的伪装（内嵌）压缩包。</summary>
public class EmbeddedArchive
{
    public string Format { get; set; } = "";
    public long Offset { get; set; }
    public string Note { get; set; } = "";

    public string OffsetHex => $"0x{Offset:X} ({Offset})";
}

/// <summary>压缩包格式。</summary>
public enum ArchiveFormat
{
    Unknown,
    SevenZip,
    Zip,
    Rar,
    GZip,
    BZip2,
    Xz,
    Tar,
    Iso,
    Cab,
    Wim,
    Zstd,
    Lz4,
    Other
}

/// <summary>格式与分卷识别结果。</summary>
public class ArchiveInfo
{
    public ArchiveFormat Format { get; set; }
    public string OriginalPath { get; set; } = "";
    /// <summary>传给 7z 的首卷路径（非分卷时等于 OriginalPath）。</summary>
    public string FirstVolumePath { get; set; } = "";
    public bool IsVolumeSet { get; set; }
    public List<string> Volumes { get; set; } = new();
    public List<string> MissingVolumes { get; set; } = new();
    public long PrefixSize { get; set; }
    public string ProbeTypeName { get; set; } = "";
    public bool IsSelfExtracting { get; set; }

    public bool HasMissingVolumes => MissingVolumes.Count > 0;
}

/// <summary>解压错误分类。</summary>
public enum ExtractErrorKind
{
    None,
    NeedsPassword,
    WrongPassword,
    MissingVolume,
    Corrupt,
    Canceled,
    Other
}

/// <summary>一次解压尝试的结果。</summary>
public class ExtractResult
{
    public bool Success { get; set; }
    public ExtractErrorKind ErrorKind { get; set; }
    public string Message { get; set; } = "";
    /// <summary>本次实际使用的密码（自动尝试命中或用户输入）。</summary>
    public string? UsedPassword { get; set; }

    public static ExtractResult Ok(string? password = null) =>
        new() { Success = true, ErrorKind = ExtractErrorKind.None, UsedPassword = password };

    public static ExtractResult Fail(ExtractErrorKind kind, string message) =>
        new() { Success = false, ErrorKind = kind, Message = message };
}

/// <summary>解压进度。</summary>
public class ExtractProgress
{
    public int Percent { get; set; }
    public string CurrentFile { get; set; } = "";
}

/// <summary>压缩包概要信息（来自 7z l）。</summary>
public class ArchiveSummary
{
    public bool IsEncrypted { get; set; }
    public int EntryCount { get; set; }
    public string TypeName { get; set; } = "";
}

/// <summary>可创建的压缩包格式。</summary>
public enum CreateFormat
{
    SevenZip,
    Zip,
    Tar,
    GZip,
    BZip2,
    Xz,
    TarGZip,
    TarBZip2,
    TarXz
}

/// <summary>压缩创建选项。</summary>
public class CreateOptions
{
    public CreateFormat Format { get; set; } = CreateFormat.SevenZip;
    public int Level { get; set; } = 5;
    public string? Password { get; set; }
    public bool EncryptHeaders { get; set; }
    public string? VolumeSize { get; set; }
    public bool VerifyAfterCreate { get; set; } = true;
    public List<string> Exclusions { get; set; } = new();
}

/// <summary>一次压缩创建任务的结果。</summary>
public class CreateResult
{
    public bool Success { get; set; }
    public ExtractErrorKind ErrorKind { get; set; }
    public string Message { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public List<string> OutputFiles { get; set; } = new();

    public static CreateResult Ok(string outputPath, IEnumerable<string> outputFiles) =>
        new()
        {
            Success = true,
            ErrorKind = ExtractErrorKind.None,
            OutputPath = outputPath,
            OutputFiles = outputFiles.ToList()
        };

    public static CreateResult Fail(ExtractErrorKind kind, string message, string outputPath = "") =>
        new() { ErrorKind = kind, Message = message, OutputPath = outputPath };
}
