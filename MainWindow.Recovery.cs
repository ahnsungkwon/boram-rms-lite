using System.IO;
using System.Windows;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private async void ReviewRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (!Writable() || _active == null) return;
        if (_dirty || _statusDirty) { Log("입력 중인 내용은 먼저 저장하거나 명시적으로 취소하세요."); return; }
        var context = _active.Context;
        var root = LocalData.JournalRoot(context);
        var allowed = Tabs.Where(t => t.Lease.Writable).Select(t => t.Context.EventRoot ?? t.Context.Root).ToArray();
        _busy = true;
        try
        {
            var reviews = await Task.Run(() => FileChanges.ReviewPending(root, allowed));
            if (reviews.Count == 0) { Log("중단된 작업 기록이 없습니다."); return; }
            foreach (var item in reviews) Log((item.CanResolve ? "검증 완료 · " : "추가 확인 필요 · ") + item.Detail + "\n" + item.Path);
            var verified = reviews.Where(r => r.CanResolve).ToArray();
            if (verified.Length == 0)
            {
                if (!TestMode) MessageBox.Show(this, "현재 파일 또는 백업이 기록과 달라 자동 정리하지 않았습니다.\n작업 기록을 펼쳐 확인하세요. 원본과 백업은 그대로 유지했습니다.", "중단 기록 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var text = $"중단 기록 {reviews.Count}개 중 {verified.Length}개는 실제 파일과 백업이 변경 전 상태임을 확인했습니다.\n\n" +
                string.Join("\n", verified.Take(5).Select(r => r.Detail)) +
                "\n\n검증된 기록만 '복구 상태 확인 완료'로 종료할까요?\n이미지·상태 파일·백업은 변경하거나 삭제하지 않습니다.\n검증되지 않은 기록은 계속 보호합니다.";
            if (TestMode || MessageBox.Show(this, text, "검증된 중단 기록 종료", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            int count = 0;
            foreach (var item in verified) { await Task.Run(() => FileChanges.ResolveUnchangedPending(item, root, allowed)); count++; }
            Log($"중단 기록 {count}개 검증 후 종료 · 남은 추가 확인 {reviews.Count - count}개 · 원본/백업 변경 없음");
        }
        catch (Exception ex) { Error(ex); }
        finally { _busy = false; }
    }
}
