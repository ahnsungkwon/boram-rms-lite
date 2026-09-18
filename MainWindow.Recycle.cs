using System.IO;
using System.Windows;
using System.Windows.Interop;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private async void Recycle_Click(object sender, RoutedEventArgs e)
    {
        if (!Writable() || _active == null) return;
        if (_dirty || _statusDirty) { Log("이름·상태 입력을 먼저 저장하거나 취소한 뒤 휴지통으로 이동하세요."); return; }
        var context = _active.Context;
        try
        {
            var plan = RecycleSelection.Prepare(context, ImageList.SelectedItems.Cast<ImageItem>());
            if (plan.Count == 0) { Log("휴지통으로 이동할 이미지를 목록에서 선택하세요."); return; }
            var message = $"선택한 이미지 {plan.Count}개만 Windows 휴지통으로 이동할까요?\n\n" +
                string.Join("\n", plan.Take(12).Select(p => Path.GetRelativePath(context.Root, p.Path))) +
                (plan.Count > 12 ? $"\n외 {plan.Count - 12}개" : "") +
                "\n\n폴더 종류와 관계없이 동일하게 처리합니다.\n선택하지 않은 이미지·jpg 사본·TXT·상태 기록은 건드리지 않습니다.\n복구는 Windows 휴지통에서 합니다.";
            if (TestMode || MessageBox.Show(this, message, "선택 이미지 휴지통 이동", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var selectedPaths = plan.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var next = ImageList.Items.Cast<ImageItem>().Skip(ImageList.SelectedIndex + 1).FirstOrDefault(i => !selectedPaths.Contains(i.FullPath))?.FullPath;
            _busy = true; _ignoreWatcherUntil = DateTime.UtcNow.AddSeconds(3);
            _thumbnailCancellation?.Cancel(); await LastPreviewTask;
            var result = await RecycleSelection.RunAsync(context, plan, new WindowInteropHelper(this).Handle);
            if (result.Done > 0) _lastLiteEdits.Remove(context.Root);
            await ReloadAsync(next); await LastPreviewTask;
            Log($"휴지통 이동 {result.Done}개 · 실패 {result.Errors.Count}개" + (result.Cancelled ? " · 취소됨" : "") +
                (result.Errors.Count == 0 ? "" : "\n" + string.Join("\n", result.Errors)));
        }
        catch (Exception ex) { Error(ex); }
        finally { _busy = false; FocusRenameInput(); }
    }
}
