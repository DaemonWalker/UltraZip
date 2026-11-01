namespace UltraZip.Models;

public sealed class ArchiveEntry
{
    public string Path { get; set; } = "";
    public ulong Size { get; set; }
    public ulong PackedSize { get; set; }
    public DateTime? Modified { get; set; }
    public string Crc { get; set; } = "";
    public string Method { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsSelected { get; set; }

    public string NormalizedPath => Path.Replace('\\', '/').Trim('/');
    public string Name
    {
        get
        {
            string path = NormalizedPath;
            int separator = path.LastIndexOf('/');
            return separator >= 0 ? path[(separator + 1)..] : path;
        }
    }
    public string DisplaySize => IsDirectory ? "" : FormatSize(Size);
    public string DisplayPackedSize => IsDirectory || PackedSize == 0 ? "" : FormatSize(PackedSize);
    public string DisplayModified => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";

    public static string FormatSize(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}
