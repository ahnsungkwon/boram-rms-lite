using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace BoramRms.Lite;

public sealed class PanelWindow : Window
{
    private readonly ContentControl _host;
    private double _normalHeight = 760;
    private bool _widget;
    public PanelWindow(string title, UIElement content, double width)
    {
        Foreground = (Brush)FindResource("TextBrush"); FontFamily = new FontFamily("Pretendard, Malgun Gothic, Segoe UI"); FontSize = 12;
        Title = "보람 RMS Lite · " + title; Width = width; Height = 760;
        MinWidth = Math.Min(width, 320); MinHeight = 160;
        WindowStyle = WindowStyle.SingleBorderWindow; Background = (Brush)FindResource("PanelBg");
        ResizeMode = ResizeMode.CanResizeWithGrip; ShowInTaskbar = false;
        var root = new DockPanel();
        var header = new DockPanel { Background = (Brush)FindResource("HeaderBg"), Margin = new Thickness(0,0,0,6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        var small = new Button { Content = "접기", Style = (Style)FindResource("QuietButton") };
        var close = new Button { Content = "↩ 복귀", Style = (Style)FindResource("QuietButton") };
        buttons.Children.Add(small); buttons.Children.Add(close); header.Children.Add(buttons);
        var top = new CheckBox { Content = "항상 위", Margin = new Thickness(12,6,0,6), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(top); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        _host = new ContentControl { Content = content }; root.Children.Add(_host); Content = root;
        top.Checked += (_, _) => Topmost = true; top.Unchecked += (_, _) => Topmost = false;
        close.Click += (_, _) => Close();
        small.Click += (_, _) =>
        {
            _widget = !_widget;
            if (_widget) { _normalHeight = Height; _host.Visibility = Visibility.Collapsed; MinHeight = 90; Height = 95; small.Content = "펼치기"; }
            else { _host.Visibility = Visibility.Visible; MinHeight = 160; Height = _normalHeight; small.Content = "접기"; }
        };
    }
    public void ReleaseContent() => _host.Content = null;
}
