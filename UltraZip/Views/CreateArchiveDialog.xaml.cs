using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using UltraZip.Models;
using UltraZip.Services;

namespace UltraZip.Views;

public partial class CreateArchiveDialog : Window
{
    private readonly ObservableCollection<string> _sources = new();
    private readonly SevenZipEngine _engine = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public CreateResult? Result { get; private set; }

    public CreateArchiveDialog(IEnumerable<string>? initialSources = null)
    {
        InitializeComponent();
        SourcesList.ItemsSource = _sources;
        FormatCombo.ItemsSource = new[]
        {
            new FormatItem("7Z（推荐）", CreateFormat.SevenZip),
            new FormatItem("ZIP", CreateFormat.Zip),
            new FormatItem("TAR", CreateFormat.Tar),
            new FormatItem("GZ（单文件）", CreateFormat.GZip),
            new FormatItem("BZ2（单文件）", CreateFormat.BZip2),
            new FormatItem("XZ（单文件）", CreateFormat.Xz),
            new FormatItem("TGZ（TAR + GZ）", CreateFormat.TarGZip),
            new FormatItem("TBZ2（TAR + BZ2）", CreateFormat.TarBZip2),
            new FormatItem("TXZ（TAR + XZ）", CreateFormat.TarXz)
        };
        FormatCombo.SelectedIndex = 0;
        if (initialSources != null) AddSources(initialSources);
        UpdateEncryptionState();
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "添加要压缩的文件", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddSources(dlg.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "添加要压缩的文件夹", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddSources(dlg.FolderNames);
    }

    private void AddSources(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if ((!File.Exists(path) && !Directory.Exists(path)) ||
                _sources.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            _sources.Add(path);
        }
        if (_sources.Count > 0)
        {
            SourcesList.SelectedIndex = _sources.Count - 1;
            if (string.IsNullOrWhiteSpace(OutputBox.Text)) SetDefaultOutputPath();
            StatusText.Text = $"已添加 {_sources.Count} 个源项目。";
        }
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedIndex < 0) return;
        _sources.RemoveAt(SourcesList.SelectedIndex);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        int index = SourcesList.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _sources.Count) return;
        _sources.Move(index, target);
        SourcesList.SelectedIndex = target;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            AddSources(paths);
    }

    private void FormatCombo_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateEncryptionState();
        UpdateOutputExtension();
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => UpdateEncryptionState();

    private void UpdateEncryptionState()
    {
        if (FormatCombo == null || PasswordBox == null) return;
        var format = SelectedFormat;
        bool canEncrypt = format is CreateFormat.SevenZip or CreateFormat.Zip;
        PasswordBox.IsEnabled = canEncrypt;
        if (!canEncrypt) PasswordBox.Clear();
        EncryptHeadersCheck.IsEnabled = format == CreateFormat.SevenZip && PasswordBox.Password.Length > 0;
        if (!EncryptHeadersCheck.IsEnabled) EncryptHeadersCheck.IsChecked = false;
        VolumeCombo.IsEnabled = canEncrypt;
        if (!canEncrypt) VolumeCombo.SelectedIndex = 0;
    }

    private CreateFormat SelectedFormat => FormatCombo.SelectedItem is FormatItem item
        ? item.Format
        : CreateFormat.SevenZip;

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        string extension = GetExtension(SelectedFormat);
        var dlg = new SaveFileDialog
        {
            Title = "选择压缩包输出路径",
            Filter = $"压缩包|*{extension}|所有文件|*.*",
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = false
        };
        if (!string.IsNullOrWhiteSpace(OutputBox.Text))
        {
            dlg.FileName = OutputBox.Text.Trim();
            string? directory = Path.GetDirectoryName(OutputBox.Text.Trim());
            if (Directory.Exists(directory)) dlg.InitialDirectory = directory;
        }
        if (dlg.ShowDialog(this) == true) OutputBox.Text = dlg.FileName;
    }

    private void SetDefaultOutputPath()
    {
        if (_sources.Count == 0) return;
        string source = _sources[0];
        string fullPath = Path.GetFullPath(source);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory == null) return;
        string name = Directory.Exists(fullPath)
            ? Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar))
            : Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(name)) name = "archive";
        string extension = GetExtension(SelectedFormat);
        if (fullPath.Equals(Path.Combine(directory, name + extension), StringComparison.OrdinalIgnoreCase))
            name += "_compressed";
        OutputBox.Text = Path.Combine(directory, name + extension);
    }

    private void UpdateOutputExtension()
    {
        if (string.IsNullOrWhiteSpace(OutputBox.Text))
        {
            SetDefaultOutputPath();
            return;
        }
        string path = OutputBox.Text.Trim();
        string[] extensions = { ".tbz2", ".tgz", ".txz", ".bz2", ".zip", ".tar", ".gz", ".xz", ".7z" };
        foreach (string extension in extensions)
        {
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
            OutputBox.Text = path[..^extension.Length] + GetExtension(SelectedFormat);
            return;
        }
    }

    private static string GetExtension(CreateFormat format) => format switch
    {
        CreateFormat.SevenZip => ".7z",
        CreateFormat.Zip => ".zip",
        CreateFormat.Tar => ".tar",
        CreateFormat.GZip => ".gz",
        CreateFormat.BZip2 => ".bz2",
        CreateFormat.Xz => ".xz",
        CreateFormat.TarGZip => ".tgz",
        CreateFormat.TarBZip2 => ".tbz2",
        CreateFormat.TarXz => ".txz",
        _ => ".7z"
    };

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_sources.Count == 0)
        {
            MessageBox.Show(this, "请先添加要压缩的文件或文件夹。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string outputPath = OutputBox.Text.Trim();
        if (string.IsNullOrEmpty(outputPath))
        {
            MessageBox.Show(this, "请选择输出路径。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (TargetFamilyExists(outputPath) && MessageBox.Show(this,
                "目标压缩包或同名分卷已存在。新包创建并校验成功后将覆盖它们，是否继续？",
                "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        string volumeText = VolumeCombo.Text.Trim();
        var options = new CreateOptions
        {
            Format = SelectedFormat,
            Level = int.Parse(((System.Windows.Controls.ComboBoxItem)LevelCombo.SelectedItem).Tag.ToString()!),
            Password = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null,
            EncryptHeaders = EncryptHeadersCheck.IsChecked == true,
            VolumeSize = volumeText.Equals("不分卷", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(volumeText) ? null : volumeText,
            VerifyAfterCreate = VerifyCheck.IsChecked == true
        };

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<ExtractProgress>(p =>
        {
            if (p.Percent >= 0)
            {
                Progress.Value = p.Percent;
                ProgressText.Text = $"{p.Percent}%";
            }
            if (!string.IsNullOrEmpty(p.CurrentFile)) StatusText.Text = p.CurrentFile;
        });
        try
        {
            Result = await _engine.CreateAsync(_sources.ToList(), outputPath, options, progress, _cts.Token);
            if (Result.Success)
            {
                Progress.Value = 100;
                ProgressText.Text = "100%";
                StatusText.Text = "压缩完成";
                SetBusy(false);
                DialogResult = true;
                Close();
                return;
            }
            StatusText.Text = Result.ErrorKind == ExtractErrorKind.Canceled ? "已取消" : "压缩失败";
            if (Result.ErrorKind != ExtractErrorKind.Canceled)
                MessageBox.Show(this, Result.Message, "压缩失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    private static bool TargetFamilyExists(string outputPath)
    {
        if (File.Exists(outputPath)) return true;
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        string fileName = Path.GetFileName(outputPath);
        return directory != null && Directory.Exists(directory) &&
            Directory.EnumerateFiles(directory, fileName + ".*")
                .Any(f => Regex.IsMatch(Path.GetExtension(f), @"^\.\d{3}$"));
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InputArea.IsEnabled = !busy;
        CreateButton.IsEnabled = !busy;
        if (!busy) return;
        Progress.Value = 0;
        ProgressText.Text = "";
        StatusText.Text = "正在创建压缩包…";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _cts?.Cancel();
            StatusText.Text = "正在取消…";
        }
        else Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        _cts?.Cancel();
        e.Cancel = true;
    }

    private sealed record FormatItem(string Name, CreateFormat Format);
}
