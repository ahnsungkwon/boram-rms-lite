using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BoramRms.Lite;

public static class ApplicantNameClipboardTests
{
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Control<T>(MainWindow window, string name) where T : class => (window.FindName(name) as T)!;
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task RunAsync(string run, Func<string, Func<Task>, Task> check)
    {
        await check("NC01 신청서 이름만 복사, 같은 항목 재바인딩·상태 저장 중복 제외", async () =>
        {
            var item = new ImageItem { FullPath = @"C:\fixture\10가상신청인.jpg", Quota = "10", Name = "가상신청인", Status = "보완", Details = "연락처 테스트 값" };
            var copied = new List<string>(); var clipboard = new ApplicantNameClipboard(copied.Add);
            Assert(await clipboard.RequestAsync(item.FullPath, item.Name, () => true) == ApplicantNameCopyResult.Copied, "최초 이름 복사 누락");
            Assert(await clipboard.RequestAsync(item.FullPath.ToUpperInvariant(), item.Name, () => true) == ApplicantNameCopyResult.Unchanged, "같은 경로 대소문자 차이로 재복사");
            Assert(copied.SequenceEqual(new[] { "가상신청인" }), "구좌·파일명·전화·상태가 이름에 혼입");
            await clipboard.RequestAsync(item.FullPath, item.Name, () => true, force: true);
            Assert(copied.Count == 2, "수동 이름 복사 재시도 불가");
        });
        await check("NC02 클립보드 잠김은 비동기 재시도 후 복사", async () =>
        {
            int attempts = 0; var delays = new List<TimeSpan>(); var copied = new List<string>();
            var clipboard = new ApplicantNameClipboard(name => { attempts++; if (attempts < 3) throw new ExternalException("synthetic clipboard busy"); copied.Add(name); }, duration => { delays.Add(duration); return Task.CompletedTask; });
            Assert(await clipboard.RequestAsync("a", "가상가", () => true) == ApplicantNameCopyResult.Copied, "일시적 잠김 후 복사 실패");
            Assert(attempts == 3 && delays.Select(d => d.TotalMilliseconds).SequenceEqual(new[] { 60d, 120d }) && copied.Single() == "가상가", "재시도 횟수·간격 오류");
        });
        await check("NC03 이전 신청서 복사 재시도가 현재 신청서 이름을 덮지 않음", async () =>
        {
            var reached = Gate(); var resume = Gate(); var copied = new List<string>();
            var clipboard = new ApplicantNameClipboard(name => { if (name == "가상가") throw new ExternalException("synthetic busy"); copied.Add(name); }, _ => { reached.TrySetResult(); return resume.Task; });
            var old = clipboard.RequestAsync("a", "가상가", () => true);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var current = await clipboard.RequestAsync("b", "가상나", () => true);
            resume.TrySetResult();
            Assert(current == ApplicantNameCopyResult.Copied && await old == ApplicantNameCopyResult.Superseded && copied.SequenceEqual(new[] { "가상나" }), "느린 이전 복사가 현재 이름을 덮음");
        });
        await check("NC04 창 종료·선택 취소 후 대기 중인 복사 중단", async () =>
        {
            var reached = Gate(); var resume = Gate(); int attempts = 0;
            var clipboard = new ApplicantNameClipboard(_ => { attempts++; throw new ExternalException("synthetic busy"); }, _ => { reached.TrySetResult(); return resume.Task; });
            var pending = clipboard.RequestAsync("a", "가상가", () => true);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(2)); clipboard.CancelPending(true); resume.TrySetResult();
            Assert(await pending == ApplicantNameCopyResult.Superseded && attempts == 1, "취소 이후 재시도 실행");
        });
        await check("NC05 잠김 지속 시 3회로 종료, 빈 이름은 클립보드를 비우지 않음", async () =>
        {
            int attempts = 0;
            var clipboard = new ApplicantNameClipboard(_ => { attempts++; throw new ExternalException("synthetic busy"); }, _ => Task.CompletedTask);
            Assert(await clipboard.RequestAsync("a", "가상가", () => true) == ApplicantNameCopyResult.Unavailable && attempts == 3, "무한 재시도 또는 너무 이른 중단");
            Assert(await clipboard.RequestAsync("blank", "  ", () => true) == ApplicantNameCopyResult.NoName && attempts == 3, "빈 값으로 기존 클립보드 삭제");
            Assert(await clipboard.RequestAsync("stale", "가상나", () => false) == ApplicantNameCopyResult.Superseded && attempts == 3, "선택 검증 없이 복사");
        });
        await check("NC06 빠른 선택은 마지막 이름만 복사, 다시 방문하면 재복사", async () =>
        {
            var copied = new List<string>(); var clipboard = new ApplicantNameClipboard(copied.Add);
            var first = clipboard.RequestAsync("a", "가상가", () => true);
            var second = clipboard.RequestAsync("b", "가상나", () => true);
            Assert(await first == ApplicantNameCopyResult.Superseded && await second == ApplicantNameCopyResult.Copied && copied.SequenceEqual(new[] { "가상나" }), "같은 UI 턴의 중간 항목이 복사됨");
            await clipboard.RequestAsync("a", "가상가", () => true);
            Assert(copied.SequenceEqual(new[] { "가상나", "가상가" }), "이전 신청서 재방문 복사 누락");
        });
        await check("NC07 실제 선택·이름 저장·상태 자동저장·수동 복사 연결", async () =>
        {
            var path = Path.Combine(run, "name-clipboard"); Directory.CreateDirectory(path);
            SelfTest.AddImage(path, "1가상가.png"); SelfTest.AddImage(path, "2가상나.png");
            var copied = new List<string>();
            var window = new MainWindow(true) { Left = -16000, Top = -16000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, NameClipboardWriterForTests = copied.Add };
            window.Show();
            try
            {
                await window.OpenFolderAsync(path); await window.LastPreviewTask; await window.LastNameCopyTask;
                Assert(copied.SequenceEqual(new[] { "가상가" }), "신청서 선택 자동 복사 미연결");
                var card = Control<CheckBox>(window, "CardCheck"); card.IsChecked = true; card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await window.LastAutoSaveTask; await window.LastNameCopyTask;
                Assert(copied.Count == 1, "카드 상태만 변경해도 클립보드 덮어씀");
                window.Rebind(); await window.LastPreviewTask; await window.LastNameCopyTask;
                Assert(copied.Count == 1, "동일 항목 재바인딩 중복 복사");
                Control<TextBox>(window, "CombinedTextBox").Text = "1가상다"; await window.SaveDraftAsync(false); await window.LastNameCopyTask;
                Assert(copied.SequenceEqual(new[] { "가상가", "가상다" }), "저장한 새 이름 복사 누락");
                await window.NavigateImageAsync(1); await window.LastPreviewTask; await window.LastNameCopyTask;
                Assert(copied.Last() == "가상나", "다음 신청서 이름 복사 누락");
                Control<TextBox>(window, "CombinedTextBox").Text = "2미저장이름";
                Control<Button>(window, "CopyApplicantNameButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await window.LastNameCopyTask;
                Assert(copied.Last() == "가상나" && copied.Count == 4, "수동 복사가 미저장 이름 또는 구좌를 복사");
            }
            finally
            {
                Control<TextBox>(window, "CombinedTextBox").Clear();
                await window.FlushStatusSavesAsync(); await window.ReloadAsync(); await window.LastPreviewTask; await window.LastNameCopyTask;
                window.Close();
            }
        });
    }
}
