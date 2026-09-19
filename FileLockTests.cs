using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
namespace BoramRms.Lite;

// All paths originate in the isolated self-test run. These are real Windows
// sharing locks, not mocked IOException results. No customer folder is opened.
public static class FileLockTests
{
    private static void Assert(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    private static T Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected failure: " + typeof(T).Name);
    }
    private static T C<T>(MainWindow w, string name) where T : class => (T)w.FindName(name);
    private static FolderContext Fixture(string root, string name)
    {
        var path = Path.Combine(root, name); Directory.CreateDirectory(path);
        SelfTest.AddImage(path, "1시험자료.png", 800, 1120);
        SelfTest.AddImage(path, "2시험자료.png", 800, 1120);
        return FolderContext.Resolve(path);
    }
    private static ImageItem First(FolderContext c) => LiteWorkspace.Load(c).First();
    private sealed class Hooks : IDisposable
    {
        private readonly Action<string>? before = LiteFileIo.TempPreparedForTests.Value;
        private readonly Action<int, IOException>? retry = LiteFileIo.RetryingForTests.Value;
        public Hooks(Action<string>? prepare = null, Action<int, IOException>? onRetry = null)
        { LiteFileIo.TempPreparedForTests.Value = prepare; LiteFileIo.RetryingForTests.Value = onRetry; }
        public void Dispose() { LiteFileIo.TempPreparedForTests.Value = before; LiteFileIo.RetryingForTests.Value = retry; }
    }
    private sealed class TempLock : IDisposable
    {
        private readonly Hooks hooks;
        private FileStream? handle;
        public string? Path { get; private set; }
        public int Retries { get; private set; }
        public int Prepared { get; private set; }
        public TempLock(int releaseOnRetry)
        {
            hooks = new Hooks(path =>
            {
                handle?.Dispose(); Path = path; Prepared++;
                handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }, (attempt, error) =>
            {
                Assert(LiteFileIo.IsSharingViolation(error), "잠금 이외 오류를 재시도함");
                Retries++;
                if (attempt >= releaseOnRetry) Release();
            });
        }
        public void Release() { handle?.Dispose(); handle = null; }
        public void Dispose() { Release(); hooks.Dispose(); }
    }
    private static async Task Window(FolderContext c, Func<MainWindow, Task> action)
    {
        var w = new MainWindow(true) { ShowInTaskbar = false, Left = -16000, Top = -16000, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Show();
        try { await w.OpenFolderAsync(c.Root); await w.LastPreviewTask; await action(w); }
        finally { await w.FlushStatusSavesAsync(); await w.ReloadAsync(); await w.LastPreviewTask; w.Close(); }
    }
    public static async Task RunAsync(string run, Action<string, Action> check, Func<string, Func<Task>, Task> checkAsync)
    {
        var root = Path.Combine(run, "file-locks"); Directory.CreateDirectory(root);
        check("L01 기존 임시파일 이동의 실제 Windows 공유 잠금 재현", () =>
        {
            var dir = Path.Combine(root, "baseline"); Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, "old-style.tmp"); var target = Path.Combine(dir, "record.json");
            File.WriteAllText(temp, "SYNTHETIC");
            using (var held = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Reject<IOException>(() => File.Move(temp, target));
                Assert(LiteFileIo.IsSharingViolation(error), "공유 잠금 오류를 재현하지 못함");
                Assert(!File.Exists(target), "실패인데 대상 파일이 생성됨");
            }
            File.WriteAllText(Path.Combine(dir, "EVIDENCE.txt"), "A real read handle without delete sharing rejects the original move operation. No external process identity is inferred.");
        });
        check("L02 최초 상태 저장: 임시파일 잠금 해제 후 자동 재시도", () =>
        {
            var c = Fixture(root, "first"); var item = First(c); var hash = SafePaths.Hash(item.FullPath);
            using (var held = new TempLock(2))
            {
                var result = LiteWorkspace.Save(c, item, null, new("보완", "주소보완", true));
                Assert(held.Retries == 2 && held.Prepared == 1 && result.Edit != null, "재시도 횟수 또는 중복 임시파일 작성");
                Assert(result.Edit!.AfterStateRevision == SafePaths.Hash(LiteWorkspace.StatePath(item.FullPath)), "저장 revision 불일치");
            }
            Assert(First(c).Card && First(c).Status == "보완" && SafePaths.Hash(item.FullPath) == hash, "최초 저장 또는 이미지 보존 실패");
        });
        check("L03 기존 상태 교체: 자동 재시도·이전 상태 백업 보존", () =>
        {
            var c = Fixture(root, "replace"); var item = First(c);
            LiteWorkspace.Save(c, item, null, new("추가", "이전", false)); item = First(c);
            var statePath = LiteWorkspace.StatePath(item.FullPath); var previous = SafePaths.Hash(statePath);
            using (var held = new TempLock(2))
            {
                LiteWorkspace.Save(c, item, null, new("보완", "변경", true));
                Assert(held.Retries == 2 && held.Prepared == 1, "교체만 재시도하지 않음");
            }
            Assert(First(c).Card && SafePaths.Hash(statePath + ".previous") == previous, "백업 또는 최종 체크 오류");
        });
        check("L04 대상 파일의 교체 잠금 해제 후 저장 성공", () =>
        {
            var dir = Path.Combine(root, "target"); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "state.json"); File.WriteAllText(path, "OLD"); var old = SafePaths.Hash(path);
            FileStream? blocker = null; var retries = 0;
            try
            {
                using var hooks = new Hooks(_ => blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), (_, _) => { retries++; blocker?.Dispose(); blocker = null; });
                LiteFileIo.AtomicReplace(path, Encoding.UTF8.GetBytes("NEW"), old, path + ".previous");
                Assert(retries == 1 && File.ReadAllText(path) == "NEW" && File.ReadAllText(path + ".previous") == "OLD", "대상 잠금 재시도 실패");
            }
            finally { blocker?.Dispose(); }
        });
        check("L05 지속 잠금: 재시도 제한·원본 보존·후속 재저장", () =>
        {
            var c = Fixture(root, "persistent"); var item = First(c);
            LiteWorkspace.Save(c, item, null, new("추가", "기존", false)); item = First(c);
            var path = LiteWorkspace.StatePath(item.FullPath); var old = SafePaths.Hash(path);
            using (var held = new TempLock(int.MaxValue))
            {
                var error = Reject<IOException>(() => LiteWorkspace.Save(c, item, null, new("보완", "새 입력", true)));
                Assert(held.Retries == 7 && LiteFileIo.IsSharingViolation(error), "무한 재시도 또는 원래 오류 손실");
                Assert(SafePaths.Hash(path) == old && File.Exists(held.Path), "실패한 교체가 기존 상태를 바꿈");
            }
            LiteWorkspace.Save(c, item, null, new("보완", "새 입력", true));
            Assert(First(c).Card && First(c).Details == "새 입력", "남은 임시파일이 후속 작업을 막음");
        });
        check("L06 재시도 사이 외부 변경 발생 시 최신 파일 덮어쓰기 거절", () =>
        {
            var dir = Path.Combine(root, "external"); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "state.json"); File.WriteAllText(path, "OLD"); var old = SafePaths.Hash(path);
            FileStream? blocker = null; var retries = 0;
            try
            {
                using var hooks = new Hooks(temp => blocker = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read), (_, _) =>
                { retries++; blocker?.Dispose(); blocker = null; File.WriteAllText(path, "EXTERNAL NEW CONTENT"); });
                var error = Reject<IOException>(() => LiteFileIo.AtomicReplace(path, Encoding.UTF8.GetBytes("DO NOT COMMIT"), old, path + ".previous"));
                Assert(retries == 1 && !LiteFileIo.IsSharingViolation(error) && File.ReadAllText(path) == "EXTERNAL NEW CONTENT", "재시도로 외부 변경을 덮어씀");
                Assert(!File.Exists(path + ".previous"), "충돌 중 백업을 수정함");
            }
            finally { blocker?.Dispose(); }
        });
        check("L07 최초 저장 대기 중 동명 대상 생성 시 덮어쓰기 거절", () =>
        {
            var dir = Path.Combine(root, "created"); Directory.CreateDirectory(dir); var path = Path.Combine(dir, "state.json");
            FileStream? blocker = null;
            try
            {
                using var hooks = new Hooks(temp => blocker = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read), (_, _) =>
                { blocker?.Dispose(); blocker = null; File.WriteAllText(path, "OTHER WRITER"); });
                Reject<IOException>(() => LiteFileIo.AtomicReplace(path, Encoding.UTF8.GetBytes("MINE"), null, null));
                Assert(File.ReadAllText(path) == "OTHER WRITER", "동시 생성 대상 덮어쓰기");
            }
            finally { blocker?.Dispose(); }
        });
        check("L08 정리 단계 잠금은 실제 실패 원인을 숨기지 않음", () =>
        {
            var dir = Path.Combine(root, "cleanup"); Directory.CreateDirectory(dir); var path = Path.Combine(dir, "state.json");
            FileStream? blocker = null;
            try
            {
                using var hooks = new Hooks(temp => { blocker = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read); throw new InvalidDataException("ORIGINAL CAUSE"); });
                var error = Reject<InvalidDataException>(() => LiteFileIo.AtomicReplace(path, Encoding.UTF8.GetBytes("DATA"), null, null));
                Assert(error.Message == "ORIGINAL CAUSE" && !File.Exists(path), "정리 예외가 원래 실패를 가림");
            }
            finally { blocker?.Dispose(); }
        });
        check("L09 회전·되돌리기 잠금: 결과 교체만 재시도·중복 회전 없음", () =>
        {
            var c = Fixture(root, "rotate"); var item = First(c); var old = SafePaths.Hash(item.FullPath);
            LiteEdit edit;
            using (var held = new TempLock(2))
            { edit = LiteWorkspace.Rotate(c, item, true); Assert(held.Prepared == 1 && held.Retries == 2, "회전 결과 중복 작성"); }
            var image = ImageProcessing.Load(item.FullPath);
            Assert(image.PixelWidth == 1120 && image.PixelHeight == 800 && SafePaths.Hash(edit.ImageBackup!) == old, "회전 또는 원본 백업 오류");
            using (var held = new TempLock(1))
            { LiteWorkspace.Undo(c, edit); Assert(held.Retries == 1, "회전 되돌리기 재시도 누락"); }
            Assert(SafePaths.Hash(item.FullPath) == old, "회전 복구 후 원본 불일치");
        });
        check("L10 권한·내용 오류는 무작정 재시도하거나 성공으로 표시하지 않음", () =>
        {
            var dir = Path.Combine(root, "permissions"); Directory.CreateDirectory(dir); var path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, "READ ONLY"); var old = SafePaths.Hash(path); File.SetAttributes(path, FileAttributes.ReadOnly); var retries = 0;
            try
            {
                using var hooks = new Hooks(onRetry: (_, _) => retries++);
                Reject<UnauthorizedAccessException>(() => LiteFileIo.AtomicReplace(path, Encoding.UTF8.GetBytes("NEW"), old, null));
                Reject<IOException>(() => LiteFileIo.Retry(() => { throw new IOException("not a sharing failure"); }));
                Assert(retries == 0 && SafePaths.Hash(path) == old, "비잠금 오류 재시도 또는 읽기전용 변경");
            }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
        });
        check("L11 상태 읽기 잠금: 해제 후 최신 상태 로드·잘못된 손상 안내 없음", () =>
        {
            var c = Fixture(root, "read"); var item = First(c); LiteWorkspace.Save(c, item, null, new("보완", "읽기 검사", true));
            FileStream? blocker = new FileStream(LiteWorkspace.StatePath(item.FullPath), FileMode.Open, FileAccess.Read, FileShare.None); var retries = 0;
            try
            {
                using var hooks = new Hooks(onRetry: (_, _) => { retries++; blocker?.Dispose(); blocker = null; });
                var saved = First(c); Assert(retries == 1 && saved.Card && saved.StateNotice.Length == 0 && saved.Details == "읽기 검사", "일시 잠금을 손상으로 오표시함");
            }
            finally { blocker?.Dispose(); }
        });
        check("L12 폴더 저장 잠금: 제한 대기 후 정상 저장", () =>
        {
            var c = Fixture(root, "gate"); var item = First(c); var dir = LiteWorkspace.StoreDirectory(item.FullPath); Directory.CreateDirectory(dir);
            FileStream? blocker = new FileStream(Path.Combine(dir, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); var retries = 0;
            try
            {
                using var hooks = new Hooks(onRetry: (_, _) => { retries++; blocker?.Dispose(); blocker = null; });
                LiteWorkspace.Save(c, item, null, new("추가", "동시 저장 대기", true));
                Assert(retries == 1 && First(c).Card, "폴더 잠금 해제 후 저장 실패");
            }
            finally { blocker?.Dispose(); }
        });
        await checkAsync("L13 빠른 체크·해제와 잠금 중 UI 응답·Space 이동·최종 상태 보존", () => Window(Fixture(root, "ui-burst"), async w =>
        {
            var input = C<TextBox>(w, "CombinedTextBox"); input.Clear();
            using (var held = new TempLock(1))
            {
                for (var i = 0; i < 13; i++)
                {
                    var box = C<CheckBox>(w, "CardCheck"); box.IsChecked = i % 2 == 0; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                var responsive = false; await w.Dispatcher.InvokeAsync(() => responsive = true);
                Assert(responsive, "잠금 대기로 UI 차단");
                await w.NavigateImageAsync(1); await w.LastPreviewTask;
                Assert(held.Prepared == 13 && held.Retries >= 13 && !w.HasPendingStatus, "빠른 체크 저장 누락");
            }
            var saved = LiteWorkspace.Load(w.Tabs.Single().Context);
            Assert(saved.First().Card && !saved.Last().Card && C<ListBox>(w, "ImageList").SelectedIndex == 1, "체크가 다음 이미지에 적용됨");
            Assert(C<Button>(w, "SaveStateButton").Visibility == Visibility.Collapsed, "성공했는데 재시도 표시 잔존");
            await w.NavigateImageAsync(-1); await w.LastPreviewTask;
            Assert(C<CheckBox>(w, "CardCheck").IsChecked == true, "이전 이동에서 체크 손실");
        }));
        await checkAsync("L14 지속 잠금 실패 시 입력·선택 보존 후 상태 다시 저장 성공", () => Window(Fixture(root, "ui-persistent"), async w =>
        {
            var input = C<TextBox>(w, "CombinedTextBox"); input.Text = "작성중"; input.Select(1, 1);
            var selected = C<ListBox>(w, "ImageList").SelectedItem;
            using (var held = new TempLock(int.MaxValue))
            {
                var box = C<CheckBox>(w, "CardCheck"); box.IsChecked = true; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(!await w.LastAutoSaveTask && held.Retries == 7, "지속 잠금을 성공으로 오표시함");
                Assert(w.HasPendingStatus && box.IsChecked == true && ReferenceEquals(selected, C<ListBox>(w, "ImageList").SelectedItem) && input.Text == "작성중", "실패에서 입력·선택 유실");
                Assert(C<Button>(w, "SaveStateButton").Visibility == Visibility.Visible && C<TextBlock>(w, "AutoSaveStatusText").Text.Contains("잠금"), "한국어 잠금·재시도 안내 없음");
            }
            C<Button>(w, "SaveStateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(await w.LastAutoSaveTask && !w.HasPendingStatus && input.Text == "작성중", "재시도가 미저장 이름을 지움");
            Assert(LiteWorkspace.Load(w.Tabs.Single().Context).First().Card, "재시도 체크가 실제 저장되지 않음");
        }));
    }
}
