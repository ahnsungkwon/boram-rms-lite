using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
namespace BoramRms.Lite;

public static class BlankNameTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);
    private static T C<T>(MainWindow w, string name) where T : class => (w.FindName(name) as T)!;
    private static void Assert(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static FolderContext Fixture(string root, string name, bool unnumbered = false)
    {
        var path = Path.Combine(root, name); Directory.CreateDirectory(path);
        if (unnumbered) { SelfTest.AddImage(path, "가상신청서.png"); SelfTest.AddImage(path, "다른신청서.png"); }
        else for (int n = 1; n <= 3; n++) SelfTest.AddImage(path, n + "가상자료.png");
        return FolderContext.Resolve(path);
    }
    private static void Click(CheckBox box, bool value) { box.IsChecked = value; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    private static async Task InWindow(FolderContext c, Func<MainWindow, Task> action)
    {
        var w = new MainWindow(true) { Left = -16000, Top = -16000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Show();
        try { await w.OpenFolderAsync(c.Root); await w.LastPreviewTask; w.UpdateLayout(); await action(w).WaitAsync(Timeout); }
        finally { w.ModifiersForTests = null; await w.FlushStatusSavesAsync(); await w.ReloadAsync(); await w.LastPreviewTask; w.Close(); }
    }
    private static async Task Key(MainWindow w, ModifierKeys modifiers, bool expectedHandled = true)
    {
        w.ModifiersForTests = () => modifiers;
        try
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), Environment.TickCount, System.Windows.Input.Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            C<TextBox>(w, "CombinedTextBox").RaiseEvent(args);
            Assert(args.Handled == expectedHandled, "키 이벤트 처리 범위 오류: " + modifiers);
            if (expectedHandled) { await w.LastShortcutTask.WaitAsync(Timeout); await w.LastPreviewTask.WaitAsync(Timeout); }
        }
        finally { w.ModifiersForTests = null; }
    }
    public static async Task RunAsync(string run, Func<string, Func<Task>, Task> check)
    {
        var root = Path.Combine(run, "blank-name"); Directory.CreateDirectory(root);
        await check("B01 빈 이름 Space: 기존 파일명·내용·수정시각 유지 후 다음", async () =>
        {
            var c = Fixture(root, "next");
            var before = Directory.GetFiles(c.Root).ToDictionary(p => p, p => (SafePaths.Hash(p), File.GetLastWriteTimeUtc(p)));
            await InWindow(c, async w =>
            {
                C<TextBox>(w, "CombinedTextBox").Clear();
                Assert(C<TextBlock>(w, "NewFileText").Text.Contains("기존 파일명 유지"), "빈 이름 미리보기 불명확");
                SelfTest.Render(w, Path.Combine(run, "blank-name-preview.png"), 1424, 941);
                await Key(w, ModifierKeys.None);
                Assert(C<ListBox>(w, "ImageList").SelectedIndex == 1 && !w.HasPendingRename, "빈 이름 다음 이동 차단");
                Assert(before.All(p => File.Exists(p.Key) && SafePaths.Hash(p.Key) == p.Value.Item1 && File.GetLastWriteTimeUtc(p.Key) == p.Value.Item2), "탐색이 원본 변경");
                Assert(!Directory.Exists(Path.Combine(c.Root, ".rmslite")), "이름 없는 탐색이 불필요한 기록 생성");
            });
        });
        await check("B02 빈 이름 Ctrl+Space: 원래 파일명 유지 후 이전", async () =>
        {
            var c = Fixture(root, "previous");
            await InWindow(c, async w =>
            {
                await w.NavigateImageAsync(1); C<TextBox>(w, "CombinedTextBox").Clear(); await Key(w, ModifierKeys.Control);
                Assert(C<ListBox>(w, "ImageList").SelectedIndex == 0 && File.Exists(Path.Combine(c.Root, "2가상자료.png")), "빈 입력 역방향 차단");
            });
        });
        await check("B03 구좌 없는 기존 파일도 빈 입력으로 탐색 가능", async () =>
        {
            var c = Fixture(root, "unnumbered", true);
            await InWindow(c, async w => { C<TextBox>(w, "CombinedTextBox").Clear(); await Key(w, ModifierKeys.None); Assert(C<ListBox>(w, "ImageList").SelectedIndex == 1 && Directory.GetFiles(c.Root, "*.png").Length == 2, "구좌 없는 기존 파일 탐색 차단"); });
        });
        await check("B04 빈 이름에서 복수 보완·카드 저장 후 다음과 이전", async () =>
        {
            var c = Fixture(root, "status-only");
            await InWindow(c, async w =>
            {
                C<TextBox>(w, "CombinedTextBox").Clear();
                foreach (var box in C<UniformGrid>(w, "RepairReasons").Children.OfType<CheckBox>().Take(2)) Click(box, true);
                Click(C<CheckBox>(w, "CardCheck"), true); await Key(w, ModifierKeys.None);
                var saved = LiteWorkspace.Load(c).Single(i => i.Quota == "1");
                Assert(saved.Card && saved.Selections!.Repairs.Split(',').Length == 2 && !w.HasPendingStatus, "빈 이름 때문에 체크 저장 차단");
                C<TextBox>(w, "CombinedTextBox").Clear(); await Key(w, ModifierKeys.Control);
                Assert(C<CheckBox>(w, "CardCheck").IsChecked == true && C<UniformGrid>(w, "RepairReasons").Children.OfType<CheckBox>().Count(b => b.IsChecked == true) == 2, "되돌아온 체크 손실");
            });
        });
        await check("B05 공백 이름 저장만: 빈 파일명 생성 없이 현재 파일 유지", async () =>
        {
            var c = Fixture(root, "whitespace");
            await InWindow(c, async w =>
            {
                C<TextBox>(w, "CombinedTextBox").Text = "   "; await w.SaveDraftAsync(false);
                Assert(C<ListBox>(w, "ImageList").SelectedIndex == 0 && C<TextBox>(w, "CombinedTextBox").Text == "1가상자료" && !w.HasPendingRename && !w.HasPendingStatus, "공백 이름 저장 오류");
                Assert(!File.Exists(Path.Combine(c.Root, ".png")), "빈 파일명 생성");
            });
        });
        await check("B06 이름 검증 실패는 상태 저장 실패로 오표시하지 않음", async () =>
        {
            var c = Fixture(root, "name-error");
            await InWindow(c, async w =>
            {
                Click(C<CheckBox>(w, "CardCheck"), true); await w.LastAutoSaveTask;
                C<TextBox>(w, "CombinedTextBox").Text = "3잘못/이름"; await w.NavigateImageAsync(1);
                Assert(C<ListBox>(w, "ImageList").SelectedIndex == 0 && w.HasPendingRename && !w.HasPendingStatus, "잘못된 파일명 보호 실패");
                Assert(!C<TextBlock>(w, "AutoSaveStatusText").Text.Contains("안 됨") && !C<TextBlock>(w, "AutoSaveStatusText").Text.Contains("실패"), "이름 오류를 상태 실패로 표시");
                Assert(C<Button>(w, "DiscardPendingButton").IsVisible && C<Button>(w, "DiscardPendingButton").IsEnabled, "복구 버튼 숨김 또는 비활성");
                C<TextBox>(w, "CombinedTextBox").Clear(); await Key(w, ModifierKeys.None);
                Assert(C<ListBox>(w, "ImageList").SelectedIndex == 1 && LiteWorkspace.Load(c).First().Card, "이름을 비운 후에도 이동 차단");
            });
        });
        await check("B07 Ctrl+Space만 이전: Shift·Alt·혼합 조합은 탐색하지 않음", async () =>
        {
            var c = Fixture(root, "modifiers");
            await InWindow(c, async w =>
            {
                await w.NavigateImageAsync(1);
                await Key(w, ModifierKeys.Shift, false); await Key(w, ModifierKeys.Alt, false); await Key(w, ModifierKeys.Control | ModifierKeys.Shift, false);
                Assert(C<ListBox>(w, "ImageList").SelectedIndex == 1, "이전 단축키 또는 혼합키가 이동함");
                await Key(w, ModifierKeys.Control); Assert(C<ListBox>(w, "ImageList").SelectedIndex == 0, "Ctrl+Space 미처리");
            });
        });
        await check("B08 실제 상태 쓰기 실패는 이유·재시도 버튼 표시 후 복구", async () =>
        {
            var c = Fixture(root, "state-error");
            await InWindow(c, async w =>
            {
                var path = w.AllItems.First().FullPath; File.SetAttributes(path, FileAttributes.ReadOnly);
                try
                {
                    C<TextBox>(w, "CombinedTextBox").Clear(); Click(C<CheckBox>(w, "CardCheck"), true); await w.LastAutoSaveTask;
                    await w.NavigateImageAsync(1);
                    Assert(w.HasPendingStatus && C<ListBox>(w, "ImageList").SelectedIndex == 0, "실제 실패를 무시하고 체크를 버림");
                    Assert(C<TextBlock>(w, "AutoSaveStatusText").Text.Contains("읽기 전용") && C<Button>(w, "SaveStateButton").IsVisible, "상태 실패 이유/재시도 숨김");
                    Assert(C<Button>(w, "DiscardPendingButton").IsVisible && C<Button>(w, "DiscardPendingButton").IsEnabled, "입력 원래대로 버튼 미표시");
                }
                finally { File.SetAttributes(path, FileAttributes.Normal); }
                C<Button>(w, "SaveStateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await w.LastAutoSaveTask;
                C<TextBox>(w, "CombinedTextBox").Clear(); await Key(w, ModifierKeys.None);
                Assert(!w.HasPendingStatus && C<ListBox>(w, "ImageList").SelectedIndex == 1 && C<Button>(w, "SaveStateButton").Visibility == Visibility.Collapsed, "상태 재시도 후 오류 잔존");
            });
        });
        await check("B09 추가 구좌 숫자가 비어 있어도 상태만 저장하고 이동", async () =>
        {
            var c = Fixture(root, "quota-optional");
            await InWindow(c, async w =>
            {
                C<TextBox>(w, "CombinedTextBox").Clear(); Click(C<CheckBox>(w, "PreChangeCheck"), true); await Key(w, ModifierKeys.None);
                Assert(C<ListBox>(w, "ImageList").SelectedIndex == 1 && File.Exists(Path.Combine(c.Root, "1가상자료.png")) && LiteWorkspace.Load(c).First().Status.Contains("입력전 변경"), "구좌 공백이 상태 이동 차단");
            });
        });
        await check("B10 빈 이름으로 다른 탭 열어도 현재 체크 보존", async () =>
        {
            var a = Fixture(root, "tabs-a"); var b = Fixture(root, "tabs-b");
            await InWindow(a, async w =>
            {
                C<TextBox>(w, "CombinedTextBox").Clear(); Click(C<CheckBox>(w, "MinorCheck"), true);
                await w.OpenFolderAsync(b.Root); await w.LastPreviewTask;
                Assert(SafePaths.Under(w.AllItems.First().FullPath, b.Root) && LiteWorkspace.Load(a).First().Status.Contains("미성년자"), "빈 이름 탭 전환 실패");
            });
        });
        await check("B11 입력 원래대로는 항상 보이는 일반 버튼, 저장한 체크 보존", async () =>
        {
            var c = Fixture(root, "visible-reset");
            await InWindow(c, async w =>
            {
                var button = C<Button>(w, "DiscardPendingButton"); Assert(button.IsVisible && button.Content.ToString() == "입력 원래대로" && button.MinHeight >= 30, "명확한 일반 버튼 아님");
                Click(C<CheckBox>(w, "CardCheck"), true); await w.LastAutoSaveTask;
                C<TextBox>(w, "CombinedTextBox").Clear(); await w.DiscardPendingInputAsync();
                Assert(C<TextBox>(w, "CombinedTextBox").Text == "1가상자료" && LiteWorkspace.Load(c).First().Card, "초안 취소가 저장된 체크 변경");
            });
        });
        await check("B12 빈 이름+미완료 메모는 상태 저장 후 이동, 중복 이름 보호 유지", async () =>
        {
            var c = Fixture(root, "memo-collision");
            await InWindow(c, async w =>
            {
                C<TextBox>(w, "CombinedTextBox").Clear(); C<TextBox>(w, "RepairMemo").Text = "추가 확인"; await Key(w, ModifierKeys.None);
                Assert(LiteWorkspace.Load(c).First().Details.Contains("추가 확인"), "빈 이름에서 메모 손실");
                C<TextBox>(w, "CombinedTextBox").Text = "1가상자료"; await w.NavigateImageAsync(1);
                Assert(C<ListBox>(w, "ImageList").SelectedIndex == 1 && Directory.GetFiles(c.Root, "*.png").Length == 3, "이름 충돌 보호 제거됨");
            });
        });
    }
}
