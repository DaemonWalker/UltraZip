using System.IO;
using System.Text.Json;
using UltraZip.Models;

namespace UltraZip.Services;

public static class ViewSettingsStore
{
    private static readonly string StoreDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraZip");
    private static readonly string StorePath = Path.Combine(StoreDirectory, "settings.json");

    public static ViewSettings Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var settings = JsonSerializer.Deserialize<ViewSettings>(File.ReadAllText(StorePath));
                if (settings != null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch
        {
        }
        return new ViewSettings();
    }

    public static void Save(ViewSettings settings)
    {
        try
        {
            settings.Normalize();
            Directory.CreateDirectory(StoreDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
