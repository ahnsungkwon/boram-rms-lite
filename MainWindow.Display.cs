using System.Windows;
using System.Windows.Controls;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private bool _syncScale, _syncFolders;
    private void InitializeDisplay()
    {
        InterfaceScale.Bind(AppLayout);
        _syncScale = true; UiScaleComboBox.ItemsSource = InterfaceScale.Options; _syncScale = false;
        InterfaceScale.Changed += DisplayScaleChanged;
        Closed += (_, _) => InterfaceScale.Changed -= DisplayScaleChanged;
        DisplayScaleChanged(null, EventArgs.Empty);
        FolderSelector.ItemsSource = Tabs;
        SyncFolderSelector();
    }
    private void DisplayScaleChanged(object? sender, EventArgs e)
    {
        _syncScale = true;
        UiScaleComboBox.SelectedItem = InterfaceScale.Options.Single(p => p.Percent == InterfaceScale.Percent);
        _syncScale = false;
        MinWidth = 1020 * InterfaceScale.Factor; MinHeight = 600 * InterfaceScale.Factor;
    }
    public bool ChangeInterfaceScale(int percent)
    {
        try { InterfaceScale.Select(percent); return true; }
        catch (Exception ex) { Error(new System.IO.IOException("화면 배율을 저장하지 못했습니다. 기존 배율을 유지합니다. " + ex.Message)); DisplayScaleChanged(null, EventArgs.Empty); return false; }
    }
    private void UiScale_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncScale || _initializing || UiScaleComboBox.SelectedItem is not ScaleOption option) return;
        ChangeInterfaceScale(option.Percent);
    }
    private void SyncFolderSelector()
    {
        if (FolderSelector == null) return;
        _syncFolders = true; FolderSelector.SelectedItem = _active; _syncFolders = false;
        FolderSelector.IsEnabled = Tabs.Count > 0;
        FolderSelector.ToolTip = _active?.Context.Root ?? "폴더 선택으로 신청서 폴더를 여세요.";
    }
    private async void FolderSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncFolders || _initializing || _selecting) return;
        var requested = FolderSelector.SelectedItem as WorkTab;
        SyncFolderSelector();
        LastNavigationTask = SelectFolderFromListAsync(requested); await LastNavigationTask;
    }
    public async Task SelectFolderFromListAsync(WorkTab? requested)
    {
        try { if (requested != null && Tabs.Contains(requested)) await SelectTabWithSaveAsync(requested); }
        finally { SyncFolderSelector(); }
    }
}
