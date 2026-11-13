using System.Windows;

namespace StartupManager.App;

public partial class MainWindow : Window
{
    private void ShowHelp(object sender, RoutedEventArgs e) => TextDialog.Show("帮助与关于",
        "开机自启管理 " + typeof(App).Assembly.GetName().Version?.ToString(3) +
        "\n\n1. 选择启动项，查看右侧来源和路径。\n2. 点击关闭并确认，程序会先保存恢复配置。\n3. 在“本工具已关闭”中选中项目，点击恢复。\n\n关闭自启不会退出或卸载软件。公共范围修改需要管理员授权。\n\n卸载：Windows 设置 → 应用 → 已安装的应用 → 开机自启管理。卸载保留备份，不会自动恢复启动项，请提前恢复需要保留的配置。\n\nMIT 开源许可 · 当前为预发布版本。");
    private readonly MainViewModel model = new();
    public MainWindow()
    {
        InitializeComponent();
        DataContext = model;
        VersionLabel.Text = "STARTUP MANAGER  /  " + typeof(App).Assembly.GetName().Version?.ToString(3);
        if (SystemParameters.WorkArea.Width < Width || SystemParameters.WorkArea.Height < Height)
            WindowState = WindowState.Maximized;
        Loaded += async (_, _) => await model.RefreshAsync();
        Closing += (_, e) =>
        {
            if (!model.IsBusy) return;
            e.Cancel = true;
            MessageBox.Show(this, "正在扫描或处理操作，请等待完成后关闭。", "请稍候");
        };
    }
}
