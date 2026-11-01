using System.Windows;
using UltraZip.Models;
using UltraZip.Services;

namespace UltraZip.Views;

public partial class PasswordPromptDialog : Window
{
    /// <summary>用户确认后的密码（取消则为 null）。</summary>
    public string? Password { get; private set; }
    public string Label => LabelBox.Text.Trim();
    public bool SaveToVault => SaveCheck.IsChecked == true;

    public PasswordPromptDialog(string archiveName)
    {
        InitializeComponent();
        Title = $"需要密码 - {archiveName}";
        VaultCombo.ItemsSource = PasswordVault.Instance.GetCandidatesOrdered();
        PasswordBox.Focus();
    }

    private void VaultCombo_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (VaultCombo.SelectedItem is PasswordEntry entry)
        {
            PasswordBox.Password = entry.Password;
            LabelBox.Text = entry.Label;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            MessageBox.Show(this, "请输入密码。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Password = PasswordBox.Password;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Password = null;
        DialogResult = false;
        Close();
    }
}
