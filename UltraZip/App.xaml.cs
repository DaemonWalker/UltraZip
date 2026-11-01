using System.Windows;
using UltraZip.Models;
using UltraZip.Services;

namespace UltraZip;

public partial class App : Application
{
    private SingleInstance? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (HandleRegistrationCommand(e.Args)) return;

        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsPrimary)
        {
            bool forwarded = await _singleInstance.TryForwardAsync(e.Args);
            if (!forwarded)
                MessageBox.Show("无法把命令转发给正在运行的 UltraZip，请稍后重试。", "UltraZip",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        StartupRequest request;
        try
        {
            request = StartupRequest.Parse(e.Args);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "命令行参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _singleInstance.RequestReceived += OnRequestReceived;
        _singleInstance.StartServer();
        var window = new MainWindow();
        MainWindow = window;
        window.ShutdownWhenIdle = request.Quiet && request.Action != StartupAction.Open;
        window.QueueCompleted += (_, _) =>
        {
            if (window.ShutdownWhenIdle) Shutdown();
        };
        if (!window.ShutdownWhenIdle) window.Show();
        window.EnqueueRequest(request);
    }

    private void OnRequestReceived(IReadOnlyList<string> args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is not MainWindow window) return;
            try
            {
                var request = StartupRequest.Parse(args);
                if (request.Action == StartupAction.Open || !request.Quiet)
                {
                    window.ShutdownWhenIdle = false;
                    if (!window.IsVisible) window.Show();
                }
                window.EnqueueRequest(request);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message, "命令行参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    private bool HandleRegistrationCommand(IReadOnlyList<string> args)
    {
        bool allUsers = args.Contains("--register-all-users", StringComparer.OrdinalIgnoreCase);
        bool register = args.Contains("--register", StringComparer.OrdinalIgnoreCase);
        bool unregister = args.Contains("--unregister", StringComparer.OrdinalIgnoreCase);
        if (!allUsers && !register && !unregister) return false;
        try
        {
            if (unregister)
            {
                ShellIntegration.Unregister();
                MessageBox.Show("已移除当前用户的 UltraZip 右键菜单和文件关联。", "完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (allUsers)
            {
                ShellIntegration.RegisterAllUsers();
                if (ShellIntegration.IsAdministrator)
                    MessageBox.Show("已为所有用户注册 UltraZip。", "完成",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                ShellIntegration.Register(false);
                MessageBox.Show("已为当前用户注册 UltraZip。Windows 11 请在“显示更多选项”中查看。",
                    "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Windows 集成操作失败：\n" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Shutdown();
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }
}
