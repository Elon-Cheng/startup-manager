using System.IO;
using System.Security.Principal;
using System.Windows;

namespace StartupManager.App;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\StartupManager.UI." + WindowsIdentity.GetCurrent().User!.Value, out bool first);
        if (!first) { MessageBox.Show("开机自启管理已经在运行，请查看任务栏。", "开机自启管理"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("操作遇到错误：" + args.Exception.Message + "\n请刷新检查实际状态。", "开机自启管理", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
