using System.Collections.ObjectModel;
using System.Windows;
using UltraZip.Models;

namespace UltraZip.Views;

public partial class ColumnSettingsDialog : Window
{
    private readonly ObservableCollection<ColumnOption> _options = new();

    public List<ArchiveColumnSettings> ResultColumns { get; private set; } = new();

    public ColumnSettingsDialog(IEnumerable<ArchiveColumnSettings> columns)
    {
        InitializeComponent();
        foreach (var column in columns)
            _options.Add(new ColumnOption(column));
        ColumnList.ItemsSource = _options;
        ColumnList.SelectedIndex = 0;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int offset)
    {
        int index = ColumnList.SelectedIndex;
        int target = index + offset;
        if (index < 0 || target < 0 || target >= _options.Count) return;
        _options.Move(index, target);
        ColumnList.SelectedIndex = target;
        ColumnList.ScrollIntoView(ColumnList.SelectedItem);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _options.Clear();
        foreach (var column in ArchiveColumnSettings.CreateDefaults())
            _options.Add(new ColumnOption(column));
        ColumnList.SelectedIndex = 0;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (_options.All(option => !option.IsVisible))
        {
            MessageBox.Show(this, "至少需要显示一列。", "列设置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ResultColumns = _options.Select(option => new ArchiveColumnSettings
        {
            Id = option.Id,
            IsVisible = option.IsVisible,
            Width = option.Width
        }).ToList();
        DialogResult = true;
    }

    private sealed class ColumnOption
    {
        public ColumnOption(ArchiveColumnSettings column)
        {
            Id = column.Id;
            IsVisible = column.IsVisible;
            Width = column.Width;
        }

        public string Id { get; }
        public bool IsVisible { get; set; }
        public double Width { get; }
        public string DisplayName => Id switch
        {
            "Name" => "名称",
            "Size" => "大小",
            "PackedSize" => "压缩后",
            "Modified" => "修改时间",
            "Crc" => "CRC",
            "Method" => "方法",
            _ => Id
        };
    }
}
