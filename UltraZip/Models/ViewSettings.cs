namespace UltraZip.Models;

public sealed class ViewSettings
{
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public bool ShowFolderTree { get; set; } = true;
    public double FolderTreeWidth { get; set; } = 220;
    public bool ShowToolbar { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;
    public bool ShowImagePreview { get; set; }
    public double ImagePreviewWidth { get; set; } = 300;
    public bool Topmost { get; set; }
    public string ViewStyle { get; set; } = "Details";
    public List<ArchiveColumnSettings> Columns { get; set; } = ArchiveColumnSettings.CreateDefaults();

    public void Normalize()
    {
        WindowWidth = Clamp(WindowWidth, 960, 3840, 1180);
        WindowHeight = Clamp(WindowHeight, 640, 2160, 760);
        FolderTreeWidth = Clamp(FolderTreeWidth, 150, 600, 220);
        ImagePreviewWidth = Clamp(ImagePreviewWidth, 220, 800, 300);
        if (ViewStyle is not ("Details" or "List")) ViewStyle = "Details";

        var existing = (Columns ?? new List<ArchiveColumnSettings>())
            .Where(column => column != null && !string.IsNullOrWhiteSpace(column.Id))
            .GroupBy(column => column.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var normalized = new List<ArchiveColumnSettings>();
        foreach (var column in Columns ?? new List<ArchiveColumnSettings>())
        {
            var defaultColumn = ArchiveColumnSettings.CreateDefaults()
                .FirstOrDefault(item => item.Id.Equals(column.Id, StringComparison.OrdinalIgnoreCase));
            if (defaultColumn == null || normalized.Any(item =>
                    item.Id.Equals(column.Id, StringComparison.OrdinalIgnoreCase))) continue;
            column.Width = Clamp(column.Width, 50, 1000, defaultColumn.Width);
            normalized.Add(column);
        }
        foreach (var column in ArchiveColumnSettings.CreateDefaults())
            if (!existing.ContainsKey(column.Id) || normalized.All(item =>
                    !item.Id.Equals(column.Id, StringComparison.OrdinalIgnoreCase)))
                normalized.Add(column);
        Columns = normalized;
    }

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) && value >= minimum && value <= maximum ? value : fallback;
}

public sealed class ArchiveColumnSettings
{
    public string Id { get; set; } = "";
    public bool IsVisible { get; set; } = true;
    public double Width { get; set; }

    public static List<ArchiveColumnSettings> CreateDefaults() => new()
    {
        new ArchiveColumnSettings { Id = "Name", Width = 270 },
        new ArchiveColumnSettings { Id = "Size", Width = 95 },
        new ArchiveColumnSettings { Id = "PackedSize", Width = 95 },
        new ArchiveColumnSettings { Id = "Modified", Width = 145 },
        new ArchiveColumnSettings { Id = "Crc", Width = 85 },
        new ArchiveColumnSettings { Id = "Method", Width = 140 }
    };
}
