using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using UltraZip.Models;
using UltraZip.Services;

namespace UltraZip.Views;

public partial class PasswordManagerWindow : Window
{
    private readonly PasswordVault _vault = PasswordVault.Instance;
    private readonly ObservableCollection<EditableEntry> _items = new();

    public PasswordManagerWindow()
    {
        InitializeComponent();
        foreach (var e in _vault.Entries)
            _items.Add(new EditableEntry(e));
        Grid.ItemsSource = _items;
        Grid.CellEditEnding += Grid_CellEditEnding;
    }

    private void Grid_CellEditEnding(object? sender,
        System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        // 等编辑值提交后再同步回库
        Dispatcher.BeginInvoke(() =>
        {
            if (e.Row.Item is EditableEntry item)
                _vault.Update(item.ToEntry());
        });
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var entry = new PasswordEntry { Label = "新密码", Password = "" };
        _vault.Add(entry.Label, entry.Password);
        // Add 里会按密码去重；用库中实际条目重建显示
        var stored = _vault.Entries.FirstOrDefault(x => x.Password == entry.Password);
        if (stored != null && _items.All(i => i.Id != stored.Id))
            _items.Add(new EditableEntry(stored));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is EditableEntry item)
        {
            _vault.Remove(item.Id);
            _items.Remove(item);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>可编辑的显示包装。</summary>
    private class EditableEntry : INotifyPropertyChanged
    {
        private readonly PasswordEntry _inner;
        public EditableEntry(PasswordEntry inner) => _inner = inner;

        public Guid Id => _inner.Id;
        public string Label { get => _inner.Label; set { _inner.Label = value; Changed(nameof(Label)); } }
        public string Password { get => _inner.Password; set { _inner.Password = value; Changed(nameof(Password)); } }
        public int HitCount => _inner.HitCount;
        public DateTime? LastUsed => _inner.LastUsed;

        public PasswordEntry ToEntry() => _inner;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
