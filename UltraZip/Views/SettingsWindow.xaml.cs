using System.Windows;
using UltraZip.Services;

namespace UltraZip.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        RunAction(() => ShellIntegration.Register(false), "已为当前用户注册。");
    }

    private void RegisterAllUsers_Click(object sender, RoutedEventArgs e)
    {
        RunAction(ShellIntegration.RegisterAllUsers,
            ShellIntegration.IsAdministrator ? "已为所有用户注册。" : "已请求 UAC 授权，请在管理员窗口中完成注册。");
    }

    private void Unregister_Click(object sender, RoutedEventArgs e)
    {
        RunAction(ShellIntegration.Unregister, "已移除当前用户的右键菜单和文件关联。");
    }

    private void RunAction(Action action, string successMessage)
    {
        try
        {
            action();
            StatusText.Text = successMessage;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Windows 集成操作失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
