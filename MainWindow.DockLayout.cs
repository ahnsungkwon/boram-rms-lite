using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private sealed record DockColumnState(double Width, double MinWidth, double MaxWidth, GridLength Gap);
    private readonly Dictionary<string, DockColumnState> _dockColumnStates = new();
    private bool _dockViewportQueued;

    // Detached width must never leak into saved settings as zero.
    internal double DockedPanelWidth(string key)
    {
        if (_dockColumnStates.TryGetValue(key, out var saved)) return saved.Width;
        var column = key == "rename" ? LeftColumn : RightColumn;
        return column.Width.IsAbsolute ? Math.Max(column.MinWidth, column.Width.Value) : column.ActualWidth;
    }
    private void SetSideDockLayout(string key, bool detached)
    {
        var left = key == "rename";
        var column = left ? LeftColumn : RightColumn;
        var gap = WorkArea.ColumnDefinitions[left ? 1 : 3];
        var host = left ? LeftHost : RightHost;
        var splitter = left ? LeftSplitter : RightSplitter;
        var restore = left ? RestoreRenamePanelButton : RestoreListPanelButton;
        if (detached)
        {
            if (!_dockColumnStates.ContainsKey(key))
                _dockColumnStates[key] = new(DockedPanelWidth(key), column.MinWidth, column.MaxWidth, gap.Width);
            host.Visibility = Visibility.Collapsed; splitter.Visibility = Visibility.Collapsed;
            column.MinWidth = 0; column.MaxWidth = 0; column.Width = new GridLength(0);
            gap.Width = new GridLength(0); restore.Visibility = Visibility.Visible;
        }
        else
        {
            if (_dockColumnStates.Remove(key, out var saved))
            {
                column.MaxWidth = saved.MaxWidth; column.MinWidth = saved.MinWidth;
                column.Width = new GridLength(saved.Width); gap.Width = saved.Gap;
            }
            host.Visibility = Visibility.Visible; splitter.Visibility = Visibility.Visible;
            restore.Visibility = Visibility.Collapsed;
        }
        QueueDockViewportRefresh();
    }
    private void QueueDockViewportRefresh()
    {
        if (_closing || _dockViewportQueued) return;
        _dockViewportQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _dockViewportQueued = false;
            if (_closing) return;
            WorkArea.UpdateLayout();
            // Dock transitions can update the camera stamp during intermediate layout.
            // Refit after the final column widths settle, rather than reusing that base width.
            if (PreviewImage.Source is BitmapSource) Fit();
        }));
    }
    private void RestoreRenamePanel_Click(object sender, RoutedEventArgs e)
    { if (!_busy && _docks.TryGetValue("rename", out var panel)) panel.Close(); }
    private void RestoreListPanel_Click(object sender, RoutedEventArgs e)
    { if (!_busy && _docks.TryGetValue("list", out var panel)) panel.Close(); }
}
