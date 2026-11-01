using System.IO;
using UltraZip.Models;

namespace UltraZip.Services;

public static class ArchiveFileDeleter
{
    public static IReadOnlyList<string> Delete(ArchiveInfo info)
    {
        IEnumerable<string> paths = info.IsVolumeSet ? info.Volumes : new[] { info.OriginalPath };
        var failures = new List<string>();
        foreach (string path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{path}：{ex.Message}");
            }
        }
        return failures;
    }
}
