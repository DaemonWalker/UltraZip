using System.Windows;
using System.Windows.Input;
using UltraZip.Models;

namespace UltraZip.Views;

public partial class EmbeddedArchivesDialog : Window
{
    /// <summary>用户选中的条目（取消则为 null）。</summary>
    public EmbeddedArchive? Selected { get; private set; }

    public EmbeddedArchivesDialog(IEnumerable<EmbeddedArchive> items)
    {
        InitializeComponent();
        List.ItemsSource = items;
        if (List.Items.Count > 0) List.SelectedIndex = 0;
    }

    private void Extract_Click(object sender, RoutedEventArgs e) => Confirm();

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

    private void Confirm()
    {
        if (List.SelectedItem is EmbeddedArchive item)
        {
            Selected = item;
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Selected = null;
        DialogResult = false;
        Close();
    }
}
