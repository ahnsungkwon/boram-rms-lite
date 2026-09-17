using System.IO;
using System.Windows;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private CancellationTokenSource? _compressionCancellation;
    private async void CompressOriginals_Click(object sender, RoutedEventArgs e)
    {
        if (!Writable() || _active == null) return;
        if (_dirty || _statusDirty) { Log("입력한 이름·상태를 먼저 저장하거나 취소하세요."); return; }
        var items = ImageList.Items.Cast<ImageItem>().ToArray();
        if (items.Length == 0) { Log("현재 표시된 이미지가 없습니다."); return; }
        var candidates = items.Count(i => i.Length >= InPlaceCompression.LimitBytes);
        if (candidates == 0) { Log("현재 표시 목록은 모두 300KB 미만입니다. 파일을 변경하지 않았습니다."); return; }
        var text = $"현재 표시된 {items.Length}개 중 300KB 이상인 {candidates}개를 줄입니다.\n\n기존 파일에 직접 덮어씁니다. 파일명과 형식은 유지합니다.\n원본 사본·백업은 만들지 않으며 되돌리기로 복구할 수 없습니다.\n이미 300KB 미만인 파일은 그대로 둡니다.\n\n이대로 실행할까요?";
        if (TestMode || MessageBox.Show(this, text, "원본 파일 직접 압축", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var context = _active.Context; var preferred = _editing?.FullPath;
        _busy = true; _compressionCancellation = new CancellationTokenSource();
        CompressionStatus.Visibility = Visibility.Visible; CompressionProgress.Maximum = items.Length; CompressionProgress.Value = 0;
        try
        {
            _thumbnailCancellation?.Cancel(); await LastPreviewTask;
            var progress = new Progress<CompressionEntry>(entry =>
            {
                CompressionProgress.Value++;
                StatusText.Text = $"직접 압축 {CompressionProgress.Value:0} / {items.Length} · {Path.GetFileName(entry.Path)}";
                if (entry.Error.Length > 0) Log("압축 제외 · " + Path.GetFileName(entry.Path) + " · " + entry.Error);
            });
            var result = await Task.Run(() => InPlaceCompression.Run(context, items, progress, _compressionCancellation.Token));
            await ReloadAsync(preferred); await LastPreviewTask;
            var changed = result.Entries.Count(i => i.Changed); var skipped = result.Entries.Count(i => i.Skipped); var failed = result.Entries.Count(i => i.Error.Length > 0);
            Log($"{(result.Cancelled ? "압축 중단" : "압축 완료")} · 변경 {changed} / 이미 작음 {skipped} / 실패 {failed} / 미처리 {items.Length - result.Entries.Count} · 기존 파일에 저장했습니다.");
        }
        catch (Exception ex) { Error(ex); }
        finally
        {
            _compressionCancellation.Dispose(); _compressionCancellation = null;
            CompressionStatus.Visibility = Visibility.Collapsed; _busy = false; FocusRenameInput();
        }
    }
    private void CancelCompression_Click(object sender, RoutedEventArgs e) => _compressionCancellation?.Cancel();
}
