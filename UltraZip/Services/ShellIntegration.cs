using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace UltraZip.Services;

public static class ShellIntegration
{
    private static readonly string[] Extensions =
    {
        ".7z", ".zip", ".rar", ".r00", ".z01", ".gz", ".gzip", ".tar",
        ".bz2", ".xz", ".tgz", ".tbz2", ".txz", ".iso", ".cab", ".wim",
        ".zst", ".zstd", ".lz4", ".001"
    };

    private static readonly (string Id, string Label, string Arguments)[] ArchiveVerbs =
    {
        ("01", "打开", "--open"),
        ("02", "解压到当前文件夹", "--extract-here --quiet"),
        ("03", "解压到此处并删除压缩文件", "--extract-here-delete --quiet"),
        ("04", "解压到…", "--extract-to-dialog"),
        ("05", "测试压缩包", "--test")
    };

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static void Register(bool allUsers)
    {
        if (allUsers && !IsAdministrator)
            throw new UnauthorizedAccessException("为所有用户注册需要管理员权限。");
        RegistryKey root = allUsers ? Registry.LocalMachine : Registry.CurrentUser;
        string executable = GetExecutablePath();
        string icon = $"\"{executable}\",0";
        using var classes = root.CreateSubKey("Software\\Classes", true);
        if (classes == null) throw new InvalidOperationException("无法打开注册表 Classes 键。");

        RegisterCreateMenu(classes, "*", executable, icon);
        RegisterCreateMenu(classes, "Directory", executable, icon);
        foreach (string extension in Extensions)
        {
            string progId = GetProgId(extension);
            RegisterMenu(classes, $@"SystemFileAssociations\{extension}",
                executable, icon, ArchiveVerbs);
            RegisterAssociatedMenu(classes, extension, progId, executable, icon);

            using (var prog = classes.CreateSubKey(progId, true))
            {
                if (prog == null) throw new InvalidOperationException("无法注册文件类型。");
                prog.SetValue("", $"{extension.TrimStart('.').ToUpperInvariant()} 压缩包");
                using var defaultIcon = prog.CreateSubKey("DefaultIcon", true);
                defaultIcon?.SetValue("", icon);
                using var command = prog.CreateSubKey(@"shell\open\command", true);
                command?.SetValue("", $"\"{executable}\" --open \"%1\"");
            }
            using var openWith = classes.CreateSubKey($@"{extension}\OpenWithProgids", true);
            openWith?.SetValue(progId, "");
        }

        using (var capabilities = root.CreateSubKey(@"Software\UltraZip\Capabilities", true))
        {
            if (capabilities == null) throw new InvalidOperationException("无法注册应用能力。");
            capabilities.SetValue("ApplicationName", "UltraZip");
            capabilities.SetValue("ApplicationDescription", "Windows 压缩与解压工具");
            using var associations = capabilities.CreateSubKey("FileAssociations", true);
            foreach (string extension in Extensions)
                associations?.SetValue(extension, GetProgId(extension));
        }
        using (var registered = root.CreateSubKey(@"Software\RegisteredApplications", true))
            registered?.SetValue("UltraZip", @"Software\UltraZip\Capabilities");
        NotifyAssociationsChanged();
    }

    public static void Unregister()
    {
        RegistryKey root = Registry.CurrentUser;
        using var classes = root.OpenSubKey("Software\\Classes", true);
        if (classes != null)
        {
            DeleteSubKeyTree(classes, @"*\shell\UltraZip.Add");
            DeleteSubKeyTree(classes, @"Directory\shell\UltraZip.Add");
            foreach (string extension in Extensions)
            {
                DeleteSubKeyTree(classes, $@"SystemFileAssociations\{extension}\shell\UltraZip");
                string? associatedProgId = GetAssociatedProgId(classes, extension);
                if (!string.IsNullOrWhiteSpace(associatedProgId))
                    DeleteSubKeyTree(classes, $@"{associatedProgId}\shell\UltraZip");
                DeleteSubKeyTree(classes, GetProgId(extension));
                using var openWith = classes.OpenSubKey($@"{extension}\OpenWithProgids", true);
                openWith?.DeleteValue(GetProgId(extension), false);
            }
        }
        DeleteSubKeyTree(root, @"Software\UltraZip\Capabilities");
        using (var registered = root.OpenSubKey(@"Software\RegisteredApplications", true))
            registered?.DeleteValue("UltraZip", false);
        NotifyAssociationsChanged();
    }

    public static void RegisterAllUsers()
    {
        if (IsAdministrator)
        {
            Register(true);
            return;
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = GetExecutablePath(),
            Arguments = "--register-all-users",
            UseShellExecute = true,
            Verb = "runas"
        };
        Process.Start(startInfo);
    }

    private static void RegisterCreateMenu(RegistryKey classes, string className,
        string executable, string icon)
    {
        using var shell = classes.CreateSubKey($@"{className}\shell\UltraZip.Add", true);
        if (shell == null) throw new InvalidOperationException("无法注册右键菜单。");
        shell.SetValue("", "添加到压缩文件…");
        shell.SetValue("Icon", icon);
        shell.SetValue("MultiSelectModel", "Player");
        using var command = shell.CreateSubKey("command", true);
        if (command == null) throw new InvalidOperationException("无法注册右键命令。");
        command.SetValue("", $"\"{executable}\" --add \"%1\"");
    }

    private static void RegisterMenu(RegistryKey classes, string className, string executable,
        string icon, (string Id, string Label, string Arguments)[] verbs)
    {
        using var shell = classes.CreateSubKey($@"{className}\shell\UltraZip", true);
        if (shell == null) throw new InvalidOperationException("无法注册右键菜单。");
        shell.DeleteValue("", false);
        shell.SetValue("MUIVerb", "UltraZip");
        shell.SetValue("Icon", icon);
        shell.SetValue("SubCommands", "");
        shell.SetValue("MultiSelectModel", "Player");
        foreach (var verb in verbs)
        {
            using var command = shell.CreateSubKey($@"shell\{verb.Id}\command", true);
            if (command == null) throw new InvalidOperationException("无法注册右键命令。");
            command.SetValue("", $"\"{executable}\" {verb.Arguments} \"%1\"");
            using var parent = shell.OpenSubKey($@"shell\{verb.Id}", true);
            parent?.SetValue("", verb.Label);
        }
    }

    private static void RegisterAssociatedMenu(RegistryKey classes, string extension,
        string ownProgId, string executable, string icon)
    {
        string? progId = GetAssociatedProgId(classes, extension);
        if (string.IsNullOrWhiteSpace(progId) || progId.Equals(ownProgId, StringComparison.OrdinalIgnoreCase))
            return;
        RegisterMenu(classes, progId, executable, icon, ArchiveVerbs);
    }

    private static string? GetAssociatedProgId(RegistryKey classes, string extension)
    {
        using var userChoice = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice");
        if (userChoice?.GetValue("ProgId") is string userProgId && !string.IsNullOrWhiteSpace(userProgId))
            return userProgId;
        using var extensionKey = classes.OpenSubKey(extension);
        return extensionKey?.GetValue("") as string;
    }

    private static string GetProgId(string extension) => "UltraZip" + extension;

    private static string GetExecutablePath() =>
        Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 UltraZip 可执行文件路径。");

    private static void DeleteSubKeyTree(RegistryKey root, string path)
    {
        try { root.DeleteSubKeyTree(path, false); }
        catch (ArgumentException) { }
    }

    private const uint ShcneAssocChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyAssociationsChanged() =>
        SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
}
