using System.Windows;
using System.Windows.Controls;

namespace StartupManager.App;

public sealed class TextDialog : Window
{
    private TextDialog(string title, string text, bool confirmation)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = title; Width = 680; Height = 520; MinWidth = 480; MinHeight = 320;
        Owner = Application.Current.MainWindow; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        if (confirmation)
        {
            var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
            cancel.Click += (_, _) => { DialogResult = false; };
            buttons.Children.Add(cancel);
        }
        var accept = new Button { Content = confirmation ? "确认执行" : "知道了", Style = (Style)FindResource("PrimaryButton"), IsDefault = !confirmation };
        accept.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(accept);
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.White, Padding = new Thickness(14) });
        Content = panel;
    }
    public static void Show(string title, string text) => new TextDialog(title, text, false).ShowDialog();
    public static bool Confirm(string title, string text) => new TextDialog(title, text, true).ShowDialog() == true;
}
