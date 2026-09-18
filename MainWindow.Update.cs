using System.IO;
using System.Windows;
namespace BoramRms.Lite;
public partial class MainWindow
{
    private void InitializeUpdater()
    {
        VersionText.Text = UpdateIdentity.CurrentVersion + " · Native / Local";
        Title = "보람 RMS Lite " + UpdateIdentity.CurrentVersion + " · 독립 신청서 작업";
        if (!TestMode) Loaded += async (_, _) =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var client = new GitHubUpdateClient(await UpdateCredential.ResolveAsync(timeout.Token));
                var release = await client.LatestAsync(timeout.Token);
                if (_closing) return;
                UpdateButton.Content = release.IsNewer ? "업데이트 " + release.Manifest.Version : "업데이트 확인";
                UpdateButton.ToolTip = release.IsNewer ? "새 배포 버전이 있습니다. 클릭해서 설치하세요." : "현재 최신 버전입니다.";
            }
            catch { if (!_closing) UpdateButton.ToolTip = "새 버전 확인을 완료하지 못했습니다. 클릭하면 인증/연결 상태를 확인할 수 있습니다."; }
        };
    }
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _loading) { Log("진행 중인 작업이 끝난 뒤 업데이트하세요."); return; }
        if (!await TrySaveCurrentAsync()) return;
        _busy = true; UpdateButton.IsEnabled = false;
        try
        {
            var dialog = new UpdateWindow { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Prepared == null) return;
            var appRoot = dialog.Prepared.AppRoot;
            if (Tabs.Any(t => SafePaths.Under(t.Context.Root, appRoot) || SafePaths.Under(appRoot, t.Context.Root))) throw new IOException("앱 설치 폴더와 업무 탭 범위가 겹칩니다. 신청서 자료는 앱 폴더와 분리해서 사용하세요.");
            SettingsStore.Save(new SavedSettings { Width = Width, Height = Height, LeftWidth = DockedPanelWidth("rename"), RightWidth = DockedPanelWidth("list"), Folders = Tabs.Select(t => t.Context.Root).ToList(), Selected = Tabs.Where(t => t.SelectedPath != null).ToDictionary(t => t.Context.Root, t => t.SelectedPath!), Thumbnails = _thumbnails });
            UpdatePackage.StartHelper(dialog.Prepared);
            _busy = false;
            Close();
        }
        catch (Exception ex) { Error(ex); }
        finally { if (!_closing) { _busy = false; UpdateButton.IsEnabled = true; } }
    }
    public async Task RestoreAfterUpdateAsync()
    {
        foreach (var folder in _previousSettings.Folders)
        {
            if (!Directory.Exists(folder)) continue;
            await OpenFolderAsync(folder);
            if (_previousSettings.Selected.TryGetValue(folder, out var selected)) Rebind(selected);
        }
        Log("업데이트 완료 · " + UpdateIdentity.CurrentVersion + " · 이전 작업 탭을 복원했습니다.");
    }
}
