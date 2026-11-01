using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using UltraZip.Models;
using UltraZip.Services;
using UltraZip.Views;

namespace UltraZip;

public partial class MainWindow : Window
{
    private readonly SevenZipEngine _engine = new();
    private readonly PasswordVault _vault = PasswordVault.Instance;
    private readonly ViewSettings _settings;
    private readonly Dictionary<string, GridViewColumn> _entryColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly GridViewColumn _selectionColumn;

    private ArchiveInfo? _info;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _listingCts;
    private CancellationTokenSource? _previewCts;
    private readonly Queue<StartupRequest> _requestQueue = new();
    private readonly List<ArchiveEntry> _entries = new();
    private readonly ObservableCollection<ArchiveEntry> _visibleEntries = new();
    private readonly Dictionary<string, TreeViewItem> _folderNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _viewTempDirs = new();
    private string? _previewTempDir;
    private long _previewRequest;
    private bool _processingRequests;
    private bool _busy;
    private string _currentFolder = "";

    private const ulong MaximumPreviewSize = 30UL * 1024 * 1024;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".tif", ".tiff"
    };

    public bool ShutdownWhenIdle { get; set; }
    public event EventHandler? QueueCompleted;

    public MainWindow()
    {
        _settings = ViewSettingsStore.Load();
        InitializeComponent();
        _selectionColumn = EntryGridView.Columns[0];
        _entryColumns["Name"] = NameColumn;
        _entryColumns["Size"] = SizeColumn;
        _entryColumns["PackedSize"] = PackedSizeColumn;
        _entryColumns["Modified"] = ModifiedColumn;
        _entryColumns["Crc"] = CrcColumn;
        _entryColumns["Method"] = MethodColumn;
        EntryList.ItemsSource = _visibleEntries;
        ApplyStoredViewSettings();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Dropdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: not null } button)
            button.ContextMenu.IsOpen = true;
    }

    private void SupportedFormats_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "可创建：7Z、ZIP、TAR、GZ、BZ2、XZ、TGZ、TBZ2、TXZ\n\n" +
            "可解压：7Z、ZIP、RAR、TAR、GZ、BZ2、XZ、ISO、CAB、WIM、ZSTD、LZ4，" +
            "以及加密包、分卷包和自解压包。",
            "支持的格式", MessageBoxButton.OK, MessageBoxImage.Information);

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "UltraZip\n安全、高效的压缩与解压工具\n\n" +
            "压缩与解压功能由独立分发的 7-Zip 提供支持。\n7-Zip 采用 LGPL 许可证。",
            "关于 UltraZip", MessageBoxButton.OK, MessageBoxImage.Information);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeWindow_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (WindowState != WindowState.Normal || e.LeftButton != MouseButtonState.Pressed) return;
        DragMove();
        e.Handled = true;
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Text = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
    }

    // ---------- 视图设置 ----------

    private void ApplyStoredViewSettings()
    {
        _settings.Normalize();
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        FolderTreeMenuItem.IsChecked = _settings.ShowFolderTree;
        ToolbarMenuItem.IsChecked = _settings.ShowToolbar;
        StatusBarMenuItem.IsChecked = _settings.ShowStatusBar;
        ImagePreviewMenuItem.IsChecked = _settings.ShowImagePreview;
        TopmostMenuItem.IsChecked = _settings.Topmost;
        ApplyViewLayout();
        ApplyColumnSettings();
        ApplyViewStyle();
    }

    private void ApplyViewLayout()
    {
        ToolbarPanel.Visibility = _settings.ShowToolbar ? Visibility.Visible : Visibility.Collapsed;
        StatusBarPanel.Visibility = _settings.ShowStatusBar || _busy
            ? Visibility.Visible : Visibility.Collapsed;
        Topmost = _settings.Topmost;

        FolderTreePanel.Visibility = _settings.ShowFolderTree ? Visibility.Visible : Visibility.Collapsed;
        FolderTreeSplitter.Visibility = FolderTreePanel.Visibility;
        FolderTreeColumn.MinWidth = _settings.ShowFolderTree ? 150 : 0;
        FolderTreeColumn.Width = _settings.ShowFolderTree
            ? new GridLength(_settings.FolderTreeWidth) : new GridLength(0);
        FolderTreeSplitterColumn.Width = new GridLength(_settings.ShowFolderTree ? 5 : 0);
        FolderTreeSplitter.Width = _settings.ShowFolderTree ? 5 : 0;

        PreviewPanel.Visibility = _settings.ShowImagePreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = PreviewPanel.Visibility;
        PreviewColumn.MinWidth = _settings.ShowImagePreview ? 220 : 0;
        PreviewColumn.Width = _settings.ShowImagePreview
            ? new GridLength(_settings.ImagePreviewWidth) : new GridLength(0);
        PreviewSplitterColumn.Width = new GridLength(_settings.ShowImagePreview ? 5 : 0);
        PreviewSplitter.Width = _settings.ShowImagePreview ? 5 : 0;
    }

    private void ApplyColumnSettings()
    {
        _settings.Normalize();
        EntryGridView.Columns.Clear();
        EntryGridView.Columns.Add(_selectionColumn);
        foreach (var setting in _settings.Columns.Where(column => column.IsVisible))
        {
            if (!_entryColumns.TryGetValue(setting.Id, out var column)) continue;
            column.Width = setting.Width;
            EntryGridView.Columns.Add(column);
        }
    }

    private void ApplyViewStyle()
    {
        bool details = _settings.ViewStyle == "Details";
        DetailsViewMenuItem.IsChecked = details;
        ListStyleMenuItem.IsChecked = !details;
        if (details)
        {
            EntryList.ItemTemplate = null;
            EntryList.View = EntryGridView;
        }
        else
        {
            EntryList.ClearValue(ListView.ViewProperty);
            EntryList.ItemTemplate = (DataTemplate)Resources["CompactEntryTemplate"];
        }
    }

    private void SaveViewSettings()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = ActualWidth;
            _settings.WindowHeight = ActualHeight;
        }
        if (_settings.ShowFolderTree && FolderTreeColumn.ActualWidth >= 150)
            _settings.FolderTreeWidth = FolderTreeColumn.ActualWidth;
        if (_settings.ShowImagePreview && PreviewColumn.ActualWidth >= 220)
            _settings.ImagePreviewWidth = PreviewColumn.ActualWidth;

        var current = _settings.Columns.ToDictionary(column => column.Id,
            StringComparer.OrdinalIgnoreCase);
        var orderedIds = EntryGridView.Columns.Skip(1)
            .Select(column => _entryColumns.FirstOrDefault(pair => ReferenceEquals(pair.Value, column)).Key)
            .Where(id => !string.IsNullOrEmpty(id)).ToList();
        orderedIds.AddRange(_settings.Columns.Select(column => column.Id)
            .Where(id => !orderedIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
        _settings.Columns = orderedIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(id =>
        {
            var setting = current[id];
            if (_entryColumns.TryGetValue(id, out var column) && double.IsFinite(column.Width))
                setting.Width = column.Width;
            return setting;
        }).ToList();
        ViewSettingsStore.Save(_settings);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F6)
        {
            StartScreen_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F7)
        {
            FolderTreeMenuItem.IsChecked = !FolderTreeMenuItem.IsChecked;
            FolderTreeMenuItem_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F8 && ArchiveCommentMenuItem.IsEnabled)
        {
            ArchiveComment_Click(sender, e);
            e.Handled = true;
        }
    }

    private void StartScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ClearArchiveView();
        _info = null;
        DestinationPanel.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Visible;
        InfoPath.Text = "尚未打开压缩包";
        InfoDetail.Text = "打开或拖入文件后即可浏览内容";
        InfoFormat.Text = "等待打开";
        Progress.Value = 0;
        ProgressText.Text = "";
        StatusText.Text = "就绪";
        UpdateCommandState();
    }

    private void FolderTreeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowFolderTree = FolderTreeMenuItem.IsChecked;
        ApplyViewLayout();
        SaveViewSettings();
    }

    private void ToolbarMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowToolbar = ToolbarMenuItem.IsChecked;
        ApplyViewLayout();
        SaveViewSettings();
    }

    private void StatusBarMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowStatusBar = StatusBarMenuItem.IsChecked;
        ApplyViewLayout();
        SaveViewSettings();
    }

    private void TopmostMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.Topmost = TopmostMenuItem.IsChecked;
        ApplyViewLayout();
        SaveViewSettings();
    }

    private void DetailsViewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.ViewStyle = "Details";
        ApplyViewStyle();
        SaveViewSettings();
    }

    private void ListStyleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.ViewStyle = "List";
        ApplyViewStyle();
        SaveViewSettings();
    }

    private void ColumnSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveViewSettings();
        var dialog = new ColumnSettingsDialog(_settings.Columns) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings.Columns = dialog.ResultColumns;
        ApplyColumnSettings();
        SaveViewSettings();
    }

    private void FolderTreeSplitter_DragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e) => SaveViewSettings();

    private void PreviewSplitter_DragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e) => SaveViewSettings();

    private async void ArchiveComment_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null || _busy) return;
        var info = _info;
        if (info.Format != ArchiveFormat.Zip)
        {
            MessageBox.Show(this, "当前支持查看 ZIP 压缩包注释，其他格式暂不支持。",
                "压缩文件注释", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            string comment = await Task.Run(() => ArchiveCommentReader.ReadZipComment(info.FirstVolumePath));
            new ArchiveCommentDialog(info.OriginalPath, comment) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "无法读取压缩包注释：\n" + ex.Message,
                "压缩文件注释", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- 命令行任务队列 ----------

    public void EnqueueRequest(StartupRequest request)
    {
        if (request.Action == StartupAction.Open && request.Paths.Count == 0) return;
        _requestQueue.Enqueue(request);
        if (!_processingRequests) _ = ProcessRequestQueueAsync();
    }

    private async Task ProcessRequestQueueAsync()
    {
        _processingRequests = true;
        try
        {
            while (_requestQueue.Count > 0)
            {
                var request = _requestQueue.Dequeue();
                try { await ProcessRequestAsync(request); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "任务失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        finally
        {
            _processingRequests = false;
            if (_requestQueue.Count == 0) QueueCompleted?.Invoke(this, EventArgs.Empty);
            else _ = ProcessRequestQueueAsync();
        }
    }

    private async Task ProcessRequestAsync(StartupRequest request)
    {
        if (request.Action == StartupAction.Add)
        {
            ShowCreateDialog(request.Paths);
            return;
        }

        foreach (string path in request.Paths)
        {
            if (!File.Exists(path))
            {
                MessageBox.Show(this, "文件不存在：\n" + path, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }
            if (request.Action == StartupAction.Open)
            {
                LoadFile(path);
                continue;
            }

            var info = FormatDetector.Detect(path);
            if (info.HasMissingVolumes)
            {
                MessageBox.Show(this, "缺少分卷，无法操作：\n" +
                    string.Join("\n", info.MissingVolumes.Select(Path.GetFileName)),
                    "缺少分卷", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            if (request.Action == StartupAction.Test)
            {
                var tested = await RunTestFlowAsync(info.FirstVolumePath,
                    Path.GetFileName(path), request.Password, !request.Quiet);
                if (tested.ErrorKind == ExtractErrorKind.Canceled) _requestQueue.Clear();
                continue;
            }

            string? dest = request.Action switch
            {
                StartupAction.ExtractHere or StartupAction.ExtractHereDelete => Path.GetDirectoryName(path),
                StartupAction.ExtractTo => request.Destination,
                _ => null
            };
            if (request.Action == StartupAction.ExtractToDialog)
            {
                var dlg = new OpenFolderDialog { Title = "选择解压目标目录" };
                if (dlg.ShowDialog(this) == true) dest = dlg.FolderName;
            }
            if (string.IsNullOrWhiteSpace(dest)) continue;
            var result = await RunExtractFlowAsync(info.FirstVolumePath, dest,
                Path.GetFileName(path), request.Password, !request.Quiet,
                request.Action == StartupAction.ExtractHereDelete ? info : null);
            if (result.ErrorKind == ExtractErrorKind.Canceled) _requestQueue.Clear();
            if (_requestQueue.Count > 0) StatusText.Text = $"等待处理 {_requestQueue.Count} 组任务…";
        }
    }

    // ---------- 拖放 / 浏览 ----------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        if (files.Length > 1 && files.All(f => Directory.Exists(f) ||
                (File.Exists(f) && !FormatDetector.LooksLikeArchive(f))))
        {
            PromptCreate(files);
            return;
        }
        LoadFile(files[0]);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择压缩包",
            Filter = "压缩包|*.7z;*.zip;*.rar;*.gz;*.bz2;*.xz;*.tar;*.iso;*.cab;*.wim;*.zst;*.zstd;*.lz4;*.001;*.z01;*.r00|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            LoadFile(dlg.FileName);
    }

    private void LoadFile(string path)
    {
        if (Directory.Exists(path))
        {
            PromptCreate(new[] { path });
            return;
        }
        if (!File.Exists(path)) return;

        ClearArchiveView();
        WelcomePanel.Visibility = Visibility.Collapsed;
        DestinationPanel.Visibility = Visibility.Visible;
        _info = FormatDetector.Detect(path);
        InfoPath.Text = path;

        var detail = new List<string>();
        if (_info.IsVolumeSet)
        {
            detail.Add($"分卷（{_info.Volumes.Count} 卷）");
            if (_info.HasMissingVolumes)
                detail.Add("缺少分卷！");
        }

        bool needsProbe = _info.Format == ArchiveFormat.Unknown;
        InfoFormat.Text = needsProbe ? "识别中…" : FormatDisplayName(_info);
        UpdateCommandState();
        DestBox.Text = DefaultDestDir(path);

        if (needsProbe)
        {
            InfoDetail.Text = "正在使用 7-Zip 识别…";
            StatusText.Text = "正在识别…";
            _ = ProbeAndFillSummaryAsync(_info, detail);
        }
        else
        {
            InfoDetail.Text = "正在读取…";
            StatusText.Text = "就绪";
            _ = FillSummaryAsync(_info, detail);
        }
    }

    private async Task ProbeAndFillSummaryAsync(ArchiveInfo info, List<string> detail)
    {
        string? typeName = null;
        try { typeName = await _engine.ProbeAsync(info.FirstVolumePath); }
        catch { }
        if (info != _info) return;

        if (typeName != null && FormatDetector.ApplyProbeType(info, typeName))
        {
            InfoFormat.Text = FormatDisplayName(info);
            UpdateCommandState();
            StatusText.Text = "就绪";
            await FillSummaryAsync(info, detail);
            return;
        }

        InfoFormat.Text = "未识别";
        InfoDetail.Text = "7-Zip 未识别，可尝试“扫描伪装压缩包”";
        UpdateCommandState();
        StatusText.Text = "就绪";
        PromptCreate(new[] { info.OriginalPath });
    }

    private static string FormatDisplayName(ArchiveInfo info)
    {
        if (info.IsSelfExtracting) return $"自解压 {FormatDetector.FormatName(info.Format)}";
        if (info.Format == ArchiveFormat.Other && !string.IsNullOrWhiteSpace(info.ProbeTypeName))
            return info.ProbeTypeName;
        return FormatDetector.FormatName(info.Format);
    }

    // ---------- 归档浏览 ----------

    private void ClearArchiveView()
    {
        ClearPreview();
        _listingCts?.Cancel();
        _listingCts = null;
        _entries.Clear();
        _visibleEntries.Clear();
        _folderNodes.Clear();
        FolderTree.Items.Clear();
        EntryFilterBox.Clear();
        _currentFolder = "";
        UpFolderButton.IsEnabled = false;
        UpFolderMenuItem.IsEnabled = false;
        UpdateCommandState();
        ArchiveStatsText.Text = "尚未打开压缩包";
    }

    private async Task LoadArchiveEntriesAsync(ArchiveInfo info)
    {
        var cts = new CancellationTokenSource();
        _listingCts = cts;
        ArchiveStatsText.Text = "正在读取归档目录…";
        try
        {
            var entries = await _engine.ListEntriesAsync(info.FirstVolumePath, ct: cts.Token);
            if (info != _info) return;
            _entries.Clear();
            _entries.AddRange(entries);
            BuildFolderTree();
            UpdateArchiveStats();
            UpdateCommandState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (info == _info) ArchiveStatsText.Text = "无法读取目录：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_listingCts, cts)) _listingCts = null;
            cts.Dispose();
        }
    }

    private void BuildFolderTree()
    {
        FolderTree.Items.Clear();
        _folderNodes.Clear();
        var root = new TreeViewItem { Header = "全部文件", Tag = "", IsExpanded = true };
        _folderNodes[""] = root;
        FolderTree.Items.Add(root);
        foreach (var entry in _entries)
        {
            string normalized = entry.NormalizedPath;
            if (normalized.Length == 0) continue;
            string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int folderCount = entry.IsDirectory ? parts.Length : parts.Length - 1;
            string parent = "";
            for (int i = 0; i < folderCount; i++)
            {
                string path = parent.Length == 0 ? parts[i] : parent + "/" + parts[i];
                if (!_folderNodes.TryGetValue(path, out var node))
                {
                    node = new TreeViewItem { Header = parts[i], Tag = path };
                    _folderNodes.Add(path, node);
                    _folderNodes[parent].Items.Add(node);
                }
                parent = path;
            }
        }
        root.IsSelected = true;
        RefreshVisibleEntries();
    }

    private void FolderTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (FolderTree.SelectedItem is not TreeViewItem item || item.Tag is not string path) return;
        _currentFolder = path;
        UpFolderButton.IsEnabled = path.Length > 0;
        UpFolderMenuItem.IsEnabled = path.Length > 0;
        RefreshVisibleEntries();
    }

    private void UpFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder.Length == 0) return;
        int separator = _currentFolder.LastIndexOf('/');
        string parent = separator >= 0 ? _currentFolder[..separator] : "";
        if (_folderNodes.TryGetValue(parent, out var node))
        {
            node.IsExpanded = true;
            node.IsSelected = true;
        }
    }

    private async void RefreshArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null || _busy) return;
        await LoadArchiveEntriesAsync(_info);
    }

    private void EntryFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshVisibleEntries();

    private void RefreshVisibleEntries()
    {
        _visibleEntries.Clear();
        string filter = EntryFilterBox.Text.Trim();
        foreach (var entry in _entries
                     .Where(e => ParentPath(e.NormalizedPath) == _currentFolder)
                     .Where(e => filter.Length == 0 ||
                         e.NormalizedPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(e => e.IsDirectory)
                     .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            _visibleEntries.Add(entry);
    }

    private static string ParentPath(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator >= 0 ? path[..separator] : "";
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _visibleEntries) entry.IsSelected = true;
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries) entry.IsSelected = false;
    }

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _visibleEntries) entry.IsSelected = !entry.IsSelected;
    }

    private async void ExtractSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        var selected = _entries.Where(entry => entry.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先勾选要解压的条目。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_info.HasMissingVolumes)
        {
            MessageBox.Show(this, "缺少分卷，无法解压。", "缺少分卷",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string dest = DestBox.Text.Trim();
        if (dest.Length == 0)
        {
            MessageBox.Show(this, "请选择解压目标目录。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var paths = selected.Select(entry => entry.Path).ToList();
        await RunEntryExtractFlowAsync(_info.FirstVolumePath, dest, paths,
            $"{Path.GetFileName(_info.OriginalPath)}（{selected.Count} 个条目）",
            null, true, true);
    }

    private async void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EntryList.SelectedItem is not ArchiveEntry entry) return;
        if (entry.IsDirectory)
        {
            if (_folderNodes.TryGetValue(entry.NormalizedPath, out var node))
            {
                node.IsExpanded = true;
                node.IsSelected = true;
            }
            return;
        }
        if (_info == null) return;
        string tempDir = CreateViewTempDirectory(_info.OriginalPath);
        var result = await RunEntryExtractFlowAsync(_info.FirstVolumePath, tempDir,
            new[] { entry.Path }, entry.Name, null, true, false);
        if (!result.Success)
        {
            TryDeleteDirectory(tempDir);
            return;
        }

        string filePath = Path.Combine(tempDir,
            entry.NormalizedPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(filePath))
            filePath = Directory.EnumerateFiles(tempDir, entry.Name, SearchOption.AllDirectories)
                .FirstOrDefault() ?? filePath;
        if (!File.Exists(filePath))
        {
            TryDeleteDirectory(tempDir);
            MessageBox.Show(this, "条目已解压，但找不到要打开的文件。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _viewTempDirs.Add(tempDir);
        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法调用系统默认程序打开：\n" + ex.Message, "打开失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- 图像预览 ----------

    private void ImagePreviewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowImagePreview = ImagePreviewMenuItem.IsChecked;
        ApplyViewLayout();
        if (_settings.ShowImagePreview) BeginSelectedPreview();
        else ClearPreview();
        SaveViewSettings();
    }

    private void PreviewClose_Click(object sender, RoutedEventArgs e)
    {
        ImagePreviewMenuItem.IsChecked = false;
        ImagePreviewMenuItem_Click(sender, e);
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings.ShowImagePreview) BeginSelectedPreview();
    }

    private void ClearPreview()
    {
        _previewRequest++;
        _previewCts?.Cancel();
        _previewCts = null;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewMessagePanel.Visibility = Visibility.Visible;
        PreviewProgress.Visibility = Visibility.Collapsed;
        PreviewStatusText.Text = "选择图片文件即可预览";
        PreviewNameText.Text = "";
        PreviewDetailText.Text = "支持 PNG、JPEG、BMP、GIF、ICO、TIFF；单条最大 30 MB";
        if (_previewTempDir != null) TryDeleteDirectory(_previewTempDir);
        _previewTempDir = null;
    }

    private void BeginSelectedPreview()
    {
        ClearPreview();
        if (!_settings.ShowImagePreview || _info == null ||
            EntryList.SelectedItem is not ArchiveEntry entry) return;
        PreviewNameText.Text = entry.Name;
        if (entry.IsDirectory)
        {
            PreviewStatusText.Text = "目录不提供图像预览";
            return;
        }
        if (!ImageExtensions.Contains(Path.GetExtension(entry.Name)))
        {
            PreviewStatusText.Text = "所选文件不是支持的图像格式";
            return;
        }
        if (entry.Size > MaximumPreviewSize)
        {
            PreviewStatusText.Text = "图片超过 30 MB，为避免占用过多内存，不予预览";
            return;
        }
        if (_info.HasMissingVolumes)
        {
            PreviewStatusText.Text = "缺少分卷，无法生成预览";
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _ = LoadPreviewAsync(_info, entry, cts, _previewRequest);
    }

    private async Task LoadPreviewAsync(ArchiveInfo info, ArchiveEntry entry,
        CancellationTokenSource cts, long request)
    {
        string? tempDir = null;
        bool IsCurrent() => request == _previewRequest && !cts.IsCancellationRequested && ReferenceEquals(info, _info);
        try
        {
            PreviewStatusText.Text = "正在解压图像预览…";
            PreviewProgress.Visibility = Visibility.Visible;
            tempDir = CreateViewTempDirectory(info.OriginalPath);
            string[] paths = { entry.Path };
            var result = await _engine.ExtractEntriesWithVaultAsync(info.FirstVolumePath,
                tempDir, paths, null, cts.Token);
            while (IsCurrent() && !result.Success &&
                   result.ErrorKind is ExtractErrorKind.NeedsPassword or ExtractErrorKind.WrongPassword)
            {
                var dialog = new PasswordPromptDialog(entry.Name) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.Password == null)
                {
                    result = ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
                    break;
                }
                result = await _engine.ExtractEntriesAsync(info.FirstVolumePath, tempDir,
                    paths, dialog.Password, null, cts.Token);
                if (result.Success)
                {
                    if (dialog.SaveToVault) _vault.Add(dialog.Label, dialog.Password);
                    _vault.RecordHit(dialog.Password);
                }
            }
            if (!IsCurrent()) return;
            if (!result.Success)
            {
                PreviewStatusText.Text = result.ErrorKind == ExtractErrorKind.Canceled
                    ? "已取消预览" : "无法生成预览：" + result.Message;
                return;
            }

            string filePath = Path.Combine(tempDir,
                entry.NormalizedPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(filePath))
                filePath = Directory.EnumerateFiles(tempDir, entry.Name, SearchOption.AllDirectories)
                    .FirstOrDefault() ?? filePath;
            if (!File.Exists(filePath))
            {
                PreviewStatusText.Text = "条目已解压，但找不到图像文件";
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 2400;
            bitmap.UriSource = new Uri(filePath);
            bitmap.EndInit();
            bitmap.Freeze();
            if (!IsCurrent()) return;

            _previewTempDir = tempDir;
            tempDir = null;
            PreviewImage.Source = bitmap;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewMessagePanel.Visibility = Visibility.Collapsed;
            PreviewDetailText.Text = $"原始大小 {ArchiveEntry.FormatSize(entry.Size)}";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent()) PreviewStatusText.Text = "已取消预览";
        }
        catch (Exception ex)
        {
            if (IsCurrent()) PreviewStatusText.Text = "无法显示图像：" + ex.Message;
        }
        finally
        {
            if (request == _previewRequest) PreviewProgress.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(_previewCts, cts)) _previewCts = null;
            if (tempDir != null) TryDeleteDirectory(tempDir);
            cts.Dispose();
        }
    }

    private static string CreateViewTempDirectory(string archivePath)
    {
        string name = $"view_{Guid.NewGuid():N}";
        string path = Path.Combine(Path.GetTempPath(), "UltraZip", name);
        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch (UnauthorizedAccessException)
        {
            string? parent = Path.GetDirectoryName(Path.GetFullPath(archivePath));
            if (parent == null) throw;
            path = Path.Combine(parent, ".ultrazip-" + name);
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveViewSettings();
        ClearPreview();
        _listingCts?.Cancel();
        _cts?.Cancel();
        foreach (string path in _viewTempDirs) TryDeleteDirectory(path);
        _viewTempDirs.Clear();
        base.OnClosed(e);
    }

    private void UpdateArchiveStats()
    {
        int files = _entries.Count(e => !e.IsDirectory);
        int folders = _entries.Count(e => e.IsDirectory);
        ulong size = _entries.Where(e => !e.IsDirectory).Aggregate(0UL, (sum, e) => sum + e.Size);
        ulong packed = _entries.Where(e => !e.IsDirectory).Aggregate(0UL, (sum, e) => sum + e.PackedSize);
        string ratio = size > 0 && packed > 0 ? $"{packed * 100.0 / size:0.#}%" : "-";
        ArchiveStatsText.Text = $"{files} 个文件，{folders} 个目录｜原始 {ArchiveEntry.FormatSize(size)}｜" +
            $"压缩后 {(packed > 0 ? ArchiveEntry.FormatSize(packed) : "-")}｜压缩率 {ratio}";
    }

    private async Task FillSummaryAsync(ArchiveInfo info, List<string> detail)
    {
        if (info.PrefixSize > 0) detail.Add($"内嵌 ZIP 偏移 0x{info.PrefixSize:X}");
        if (info.IsSelfExtracting) detail.Add("自解压程序，请勿直接运行未知来源的 exe");
        try
        {
            var summary = await _engine.GetSummaryAsync(info.FirstVolumePath);
            if (info != _info) return; // 期间用户换了文件
            if (summary.IsEncrypted) detail.Add("已加密");
            if (summary.EntryCount > 0) detail.Add($"{summary.EntryCount} 个条目");
            InfoDetail.Text = detail.Count > 0 ? string.Join("，", detail) : "-";
        }
        catch
        {
            if (info == _info)
                InfoDetail.Text = detail.Count > 0 ? string.Join("，", detail) : "-";
        }
        if (info == _info) await LoadArchiveEntriesAsync(info);
    }

    private static string DefaultDestDir(string archivePath)
    {
        var dir = Path.GetDirectoryName(archivePath) ?? ".";
        var name = Path.GetFileName(archivePath);
        // 去掉分卷与格式后缀：a.7z.001→a，a.part1.rar→a，a.zip→a
        name = System.Text.RegularExpressions.Regex.Replace(name,
            @"(\.part\d+)?\.(7z|zip|rar|gz|bz2|xz|tar|tgz|iso|cab|wim|zst|zstd|lz4|exe|001|r\d{2}|z\d{2}|\d{3,})$",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        name = System.Text.RegularExpressions.Regex.Replace(name,
            @"(\.part\d+)?\.(7z|zip|rar|gz|bz2|xz|tar|iso|cab|wim|zst|zstd|lz4|exe)$",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (string.IsNullOrWhiteSpace(name)) name = "output";
        return Path.Combine(dir, name);
    }

    private void BrowseDest_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择解压目标目录" };
        if (dlg.ShowDialog(this) == true)
            DestBox.Text = dlg.FolderName;
    }

    // ---------- 解压 ----------

    private async void Extract_Click(object sender, RoutedEventArgs e) =>
        await ExtractFromWindowAsync(false);

    private async void ExtractDelete_Click(object sender, RoutedEventArgs e) =>
        await ExtractFromWindowAsync(true);

    private async void ExtractHere_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        DestBox.Text = Path.GetDirectoryName(_info.OriginalPath) ?? "";
        await ExtractFromWindowAsync(false);
    }

    private async void ExtractNamedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        DestBox.Text = DefaultDestDir(_info.OriginalPath);
        await ExtractFromWindowAsync(false);
    }

    private async Task ExtractFromWindowAsync(bool deleteArchive)
    {
        if (_info == null) return;
        var info = _info;

        if (info.HasMissingVolumes)
        {
            MessageBox.Show(this,
                "缺少分卷，无法解压：\n" +
                string.Join("\n", info.MissingVolumes.Select(Path.GetFileName)),
                "缺少分卷", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dest = DestBox.Text.Trim();
        if (string.IsNullOrEmpty(dest))
        {
            MessageBox.Show(this, "请选择解压目标目录。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunExtractFlowAsync(info.FirstVolumePath, dest, Path.GetFileName(info.OriginalPath),
            deleteArchive: deleteArchive ? info : null);
    }

    /// <summary>完整解压流程：自动试密码库 → 弹窗询问 → 结果反馈。</summary>
    private async Task<ExtractResult> RunExtractFlowAsync(string archivePath, string destDir,
        string displayName, string? initialPassword = null, bool allowPasswordPrompt = true,
        ArchiveInfo? deleteArchive = null)
    {
        SetBusy(true);
        var progress = new Progress<ExtractProgress>(p =>
        {
            if (p.Percent >= 0)
            {
                Progress.Value = p.Percent;
                ProgressText.Text = $"{p.Percent}%";
            }
            if (!string.IsNullOrEmpty(p.CurrentFile))
                StatusText.Text = p.CurrentFile;
        });

        _cts = new CancellationTokenSource();
        ExtractResult result;
        try
        {
            result = initialPassword == null
                ? await _engine.ExtractWithVaultAsync(archivePath, destDir, progress, _cts.Token)
                : await _engine.ExtractAsync(archivePath, destDir, initialPassword, progress, _cts.Token);

            while (allowPasswordPrompt && !result.Success &&
                   result.ErrorKind is ExtractErrorKind.NeedsPassword or ExtractErrorKind.WrongPassword)
            {
                var dlg = new PasswordPromptDialog(displayName) { Owner = this };
                if (dlg.ShowDialog() != true || dlg.Password == null)
                {
                    result = ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
                    break;
                }
                StatusText.Text = "正在尝试输入的密码…";
                result = await _engine.ExtractAsync(archivePath, destDir, dlg.Password,
                    progress, _cts.Token);
                if (result.Success)
                {
                    if (dlg.SaveToVault)
                        _vault.Add(dlg.Label, dlg.Password);
                    _vault.RecordHit(dlg.Password);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
        }
        catch (Exception ex)
        {
            result = ExtractResult.Fail(ExtractErrorKind.Other, ex.Message);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }

        if (result.Success)
        {
            Progress.Value = 100;
            ProgressText.Text = "100%";
            StatusText.Text = "解压完成";
            string message = $"解压完成：\n{destDir}";
            var image = MessageBoxImage.Information;
            if (deleteArchive != null)
            {
                var failures = ArchiveFileDeleter.Delete(deleteArchive);
                if (failures.Count == 0)
                {
                    StatusText.Text = "解压完成，压缩文件已删除";
                    message += "\n\n压缩文件已永久删除。";
                    CloseDeletedArchive(deleteArchive, destDir);
                }
                else
                {
                    StatusText.Text = "解压完成，但压缩文件删除失败";
                    message += "\n\n以下压缩文件未能删除：\n" + string.Join("\n", failures);
                    image = MessageBoxImage.Warning;
                }
            }
            MessageBox.Show(this, message, "完成", MessageBoxButton.OK, image);
        }
        else if (result.ErrorKind != ExtractErrorKind.Canceled)
        {
            StatusText.Text = "解压失败";
            var title = result.ErrorKind switch
            {
                ExtractErrorKind.MissingVolume => "缺少分卷或文件不完整",
                ExtractErrorKind.Corrupt => "文件损坏或不是有效的压缩包",
                ExtractErrorKind.WrongPassword => "密码错误",
                ExtractErrorKind.NeedsPassword => "需要密码",
                _ => "解压失败"
            };
            MessageBox.Show(this, result.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            StatusText.Text = "已取消";
        }
        return result;
    }

    private void CloseDeletedArchive(ArchiveInfo info, string destDir)
    {
        if (!ReferenceEquals(_info, info)) return;
        ClearArchiveView();
        _info = null;
        UpdateCommandState();
        DestinationPanel.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Visible;
        InfoPath.Text = "压缩文件已删除";
        InfoDetail.Text = $"内容已解压到：{destDir}";
        InfoFormat.Text = "已删除";
    }

    private async Task<ExtractResult> RunEntryExtractFlowAsync(string archivePath, string destDir,
        IReadOnlyList<string> paths, string displayName, string? initialPassword,
        bool allowPasswordPrompt, bool showCompletion)
    {
        SetBusy(true);
        var progress = new Progress<ExtractProgress>(p =>
        {
            if (p.Percent >= 0)
            {
                Progress.Value = p.Percent;
                ProgressText.Text = $"{p.Percent}%";
            }
            if (!string.IsNullOrEmpty(p.CurrentFile)) StatusText.Text = p.CurrentFile;
        });
        _cts = new CancellationTokenSource();
        ExtractResult result;
        try
        {
            result = initialPassword == null
                ? await _engine.ExtractEntriesWithVaultAsync(archivePath, destDir, paths, progress, _cts.Token)
                : await _engine.ExtractEntriesAsync(archivePath, destDir, paths, initialPassword,
                    progress, _cts.Token);
            while (allowPasswordPrompt && !result.Success &&
                   result.ErrorKind is ExtractErrorKind.NeedsPassword or ExtractErrorKind.WrongPassword)
            {
                var dlg = new PasswordPromptDialog(displayName) { Owner = this };
                if (dlg.ShowDialog() != true || dlg.Password == null)
                {
                    result = ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
                    break;
                }
                result = await _engine.ExtractEntriesAsync(archivePath, destDir, paths,
                    dlg.Password, progress, _cts.Token);
                if (result.Success)
                {
                    if (dlg.SaveToVault) _vault.Add(dlg.Label, dlg.Password);
                    _vault.RecordHit(dlg.Password);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
        }
        catch (Exception ex)
        {
            result = ExtractResult.Fail(ExtractErrorKind.Other, ex.Message);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }

        if (result.Success)
        {
            Progress.Value = 100;
            ProgressText.Text = "100%";
            StatusText.Text = "解压完成";
            if (showCompletion)
                MessageBox.Show(this, $"已解压 {paths.Count} 个条目到：\n{destDir}", "完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (result.ErrorKind != ExtractErrorKind.Canceled)
        {
            StatusText.Text = "解压失败";
            MessageBox.Show(this, result.Message, "解压失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else StatusText.Text = "已取消";
        return result;
    }

    private async void TestArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null || _busy) return;
        if (_info.HasMissingVolumes)
        {
            MessageBox.Show(this, "缺少分卷，无法测试。", "缺少分卷",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await RunTestFlowAsync(_info.FirstVolumePath, Path.GetFileName(_info.OriginalPath), null, true);
    }

    private async Task<ExtractResult> RunTestFlowAsync(string archivePath, string displayName,
        string? initialPassword, bool allowPasswordPrompt)
    {
        SetBusy(true);
        var progress = new Progress<ExtractProgress>(p =>
        {
            if (p.Percent >= 0)
            {
                Progress.Value = p.Percent;
                ProgressText.Text = $"{p.Percent}%";
            }
            if (!string.IsNullOrEmpty(p.CurrentFile)) StatusText.Text = p.CurrentFile;
        });
        _cts = new CancellationTokenSource();
        ExtractResult result;
        try
        {
            result = initialPassword == null
                ? await _engine.TestWithVaultAsync(archivePath, progress, _cts.Token)
                : await _engine.TestAsync(archivePath, initialPassword, progress, _cts.Token);
            while (allowPasswordPrompt && !result.Success &&
                   result.ErrorKind is ExtractErrorKind.NeedsPassword or ExtractErrorKind.WrongPassword)
            {
                var dlg = new PasswordPromptDialog(displayName) { Owner = this };
                if (dlg.ShowDialog() != true || dlg.Password == null)
                {
                    result = ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
                    break;
                }
                result = await _engine.TestAsync(archivePath, dlg.Password, progress, _cts.Token);
                if (result.Success)
                {
                    if (dlg.SaveToVault) _vault.Add(dlg.Label, dlg.Password);
                    _vault.RecordHit(dlg.Password);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
        }
        catch (Exception ex)
        {
            result = ExtractResult.Fail(ExtractErrorKind.Other, ex.Message);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }

        if (result.Success)
        {
            Progress.Value = 100;
            ProgressText.Text = "100%";
            StatusText.Text = "测试通过";
            MessageBox.Show(this, $"压缩包完整，测试通过：\n{displayName}", "测试完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (result.ErrorKind != ExtractErrorKind.Canceled)
        {
            StatusText.Text = "测试失败";
            MessageBox.Show(this, result.Message, "测试失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else StatusText.Text = "已取消";
        return result;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ApplyViewLayout();
        UpdateCommandState();
        CancelButton.IsEnabled = busy;
        if (!busy) return;
        Progress.Value = 0;
        ProgressText.Text = "";
    }

    private void UpdateCommandState()
    {
        bool canUseArchive = !_busy && _info is { Format: not ArchiveFormat.Unknown };
        bool canSelect = !_busy && _entries.Count > 0;
        ExtractButton.IsEnabled = canUseArchive;
        ExtractMenuButton.IsEnabled = canUseArchive;
        ExtractDeleteMenuItem.IsEnabled = canUseArchive;
        TestMenuItem.IsEnabled = canUseArchive;
        RefreshMenuItem.IsEnabled = canUseArchive;
        ArchiveCommentMenuItem.IsEnabled = canUseArchive;
        StatusBarMenuItem.IsEnabled = !_busy;
        ExtractSelectedMenuItem.IsEnabled = canSelect;
        EditExtractSelectedMenuItem.IsEnabled = canSelect;
        SelectAllButton.IsEnabled = canSelect;
        SelectMenuButton.IsEnabled = canSelect;
        SelectAllMenuItem.IsEnabled = canSelect;
        ClearSelectionMenuItem.IsEnabled = canSelect;
        InvertSelectionMenuItem.IsEnabled = canSelect;
        CreateButton.IsEnabled = !_busy;
        NewArchiveMenuItem.IsEnabled = !_busy;
    }

    // ---------- 伪装文件扫描 ----------

    private async void ScanPolyglot_Click(object sender, RoutedEventArgs e)
    {
        var path = _info?.OriginalPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            var dlg = new OpenFileDialog { Title = "选择要扫描的文件", Filter = "所有文件|*.*" };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
            LoadFile(path);
        }

        SetBusy(true);
        StatusText.Text = "正在扫描伪装压缩包…";
        var scanProgress = new Progress<int>(p =>
        {
            Progress.Value = p;
            ProgressText.Text = $"{p}%";
        });

        List<EmbeddedArchive> found;
        try
        {
            _cts = new CancellationTokenSource();
            found = await PolyglotScanner.ScanAsync(path, scanProgress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
            StatusText.Text = "已取消";
            return;
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }

        if (found.Count == 0)
        {
            StatusText.Text = "未扫描到内嵌压缩包";
            MessageBox.Show(this, "未在该文件中发现压缩包特征。", "扫描结果",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new EmbeddedArchivesDialog(found) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Selected == null)
        {
            StatusText.Text = "就绪";
            return;
        }

        await ExtractEmbeddedAsync(path, dialog.Selected);
    }

    /// <summary>解压内嵌压缩包：先直接让 7z 开原文件，失败则从偏移切出临时副本。</summary>
    private async Task ExtractEmbeddedAsync(string sourcePath, EmbeddedArchive item)
    {
        var dest = DestBox.Text.Trim();
        if (string.IsNullOrEmpty(dest))
        {
            var dlg = new OpenFolderDialog { Title = "选择解压目标目录" };
            if (dlg.ShowDialog(this) != true) return;
            dest = dlg.FolderName;
        }

        StatusText.Text = "尝试直接解压原文件…";
        var result = await RunExtractFlowAsync(sourcePath, dest,
            $"{Path.GetFileName(sourcePath)} @ {item.Format} 0x{item.Offset:X}");
        if (result.Success) return;

        // 直接打开失败：从偏移处切出临时副本再解压
        var answer = MessageBox.Show(this,
            $"7-Zip 无法直接打开原文件。\n\n是否从偏移 0x{item.Offset:X} 处切出 {item.Format} 数据副本后再试？",
            "直接解压失败", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var ext = item.Format switch
        {
            "ZIP" => ".zip",
            "7z" => ".7z",
            "RAR4" or "RAR5" => ".rar",
            "GZip" => ".gz",
            _ => ".bin"
        };
        var tmpDir = Path.Combine(Path.GetTempPath(), "UltraZip");
        Directory.CreateDirectory(tmpDir);
        var tmpPath = Path.Combine(tmpDir, $"carved_{Guid.NewGuid():N}{ext}");
        try
        {
            StatusText.Text = "正在切出数据副本…";
            await Task.Run(() => CarveFromOffset(sourcePath, item.Offset, tmpPath));
            await RunExtractFlowAsync(tmpPath, dest,
                $"{Path.GetFileName(sourcePath)} @ {item.Format} 0x{item.Offset:X}（切出副本）");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "切出数据失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* 忽略 */ }
        }
    }

    private static void CarveFromOffset(string sourcePath, long offset, string destPath)
    {
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        input.Seek(offset, SeekOrigin.Begin);
        input.CopyTo(output);
    }

    // ---------- 压缩创建 ----------

    private void CreateArchive_Click(object sender, RoutedEventArgs e) => ShowCreateDialog();

    private void PromptCreate(IEnumerable<string> sources)
    {
        var sourceList = sources.ToList();
        var answer = MessageBox.Show(this,
            "所选内容不是已识别的压缩包。是否创建新压缩包？\n选择“否”仍可使用伪装压缩包扫描。",
            "新建压缩包", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) ShowCreateDialog(sourceList);
    }

    private void ShowCreateDialog(IEnumerable<string>? sources = null)
    {
        var dialog = new CreateArchiveDialog(sources) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { Success: true } result) return;
        StatusText.Text = "压缩完成";
        string volumes = result.OutputFiles.Count > 1 ? $"\n共 {result.OutputFiles.Count} 个分卷" : "";
        MessageBox.Show(this, $"压缩包已创建：\n{result.OutputPath}{volumes}", "完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow { Owner = this };
        window.ShowDialog();
    }

    // ---------- 密码管理器 ----------

    private void PasswordManager_Click(object sender, RoutedEventArgs e)
    {
        var w = new PasswordManagerWindow { Owner = this };
        w.ShowDialog();
    }
}
