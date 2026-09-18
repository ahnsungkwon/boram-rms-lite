using System.IO;
using System.Windows;
using Microsoft.Win32;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private CancellationTokenSource? _folderScan;
    private bool _openingFolders;
    private async Task BrowseFoldersAsync()
    {
        if (_busy || _loading || _openingFolders || _navigating) return;
        var dialog = new OpenFolderDialog { Title = "신청서 폴더 또는 여러 폴더가 들어 있는 상위 폴더 선택" };
        if (dialog.ShowDialog(this) != true) return;
        _folderScan = new CancellationTokenSource(); _openingFolders = true;
        try
        {
            Log("선택한 위치에서 신청서 폴더를 찾고 있습니다…");
            var scan = await Task.Run(() => FolderDiscovery.Scan(dialog.FolderName, _folderScan.Token));
            if (_closing) return;
            IReadOnlyList<string> chosen;
            if (scan.Choices.Count == 1 && scan.Warnings.Count == 0) chosen = new[] { scan.Choices[0].Path };
            else
            {
                var picker = new FolderPickerWindow(dialog.FolderName, scan) { Owner = this };
                if (picker.ShowDialog() != true) { Log("폴더 선택을 취소했습니다. 기존 작업은 그대로입니다."); return; }
                chosen = picker.SelectedPaths;
            }
            await OpenChosenFoldersAsync(chosen);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing) Error(ex); }
        finally { _folderScan.Dispose(); _folderScan = null; _openingFolders = false; }
    }
    public async Task OpenChosenFoldersAsync(IEnumerable<string> paths)
    {
        if (_busy || _loading || _navigating || _closing || !await TrySaveCurrentAsync()) return;
        var unique = paths.Select(p => FolderContext.Resolve(p).Root).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int opened = 0;
        foreach (var path in unique)
        {
            if (_closing) break;
            await OpenFolderAsync(path); await LastPreviewTask;
            if (_active == null || !SafePaths.Same(_active.Context.Root, path)) break;
            opened++;
        }
        SyncFolderSelector();
        if (!_closing) Log($"선택한 폴더 {opened}/{unique.Length}개 열기 · 신청서 목록 위에서 폴더를 바꿀 수 있습니다.");
    }
}
