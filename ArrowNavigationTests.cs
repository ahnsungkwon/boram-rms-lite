using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace BoramRms.Lite;

public static class ArrowNavigationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);
    private static T C<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("컨트롤 없음: " + name);
    private static void Assert(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    private static async Task InWindow(string root, string name, Func<MainWindow, FolderContext, Task> test)
    {
        var folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        for (var index = 1; index <= 4; index++) SelfTest.AddImage(folder, index + "가상자료.png");
        var context = FolderContext.Resolve(folder);
        var window = new MainWindow(testMode: true)
        {
            Left = -16000, Top = -16000, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ModifiersForTests = () => ModifierKeys.None,
            NameClipboardWriterForTests = _ => { }
        };
        window.Show();
        try
        {
            await window.OpenFolderAsync(context.Root).WaitAsync(Timeout);
            await window.LastPreviewTask.WaitAsync(Timeout);
            window.UpdateLayout();
            await test(window, context).WaitAsync(Timeout);
        }
        finally
        {
            window.ModifiersForTests = null;
            await window.FlushStatusSavesAsync().WaitAsync(Timeout);
            if (window.IsVisible)
            {
                C<TextBox>(window, "CombinedTextBox").Clear();
                await window.ReloadAsync().WaitAsync(Timeout);
                await window.LastPreviewTask.WaitAsync(Timeout);
                window.Close();
            }
        }
    }

    private static KeyEventArgs Raise(UIElement source, Key key, bool repeat = false, bool ime = false,
        RoutedEvent? routedEvent = null)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source),
            Environment.TickCount, key) { RoutedEvent = routedEvent ?? Keyboard.PreviewKeyDownEvent };
        // Set only this synthetic event's flags; never send operating-system input.
        if (repeat) SetEventFlag(args, "SetRepeat", true);
        if (ime) SetEventFlag(args, "MarkImeProcessed");
        source.RaiseEvent(args);
        return args;
    }

    private static void SetEventFlag(KeyEventArgs args, string name, params object[] arguments)
    {
        var method = typeof(KeyEventArgs).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("합성 키 이벤트 설정 함수 없음: " + name);
        method.Invoke(args, arguments);
    }

    private static async Task Arrow(MainWindow window, string source, Key key, bool repeat = false)
    {
        var args = Raise(C<UIElement>(window, source), key, repeat);
        Assert(args.Handled, "방향키 미처리: " + source + " / " + key);
        await window.LastShortcutTask.WaitAsync(Timeout);
        await window.LastPreviewTask.WaitAsync(Timeout);
    }

    private static void Click(CheckBox box, bool value)
    { box.IsChecked = value; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }

    public static async Task RunAsync(string run, Func<string, Func<Task>, Task> check)
    {
        var root = Path.Combine(run, "arrow-navigation"); Directory.CreateDirectory(root);
        await check("AR01 이름·목록·이미지 위아래 이동 및 첫/끝 경계", () => InWindow(root, "directions", async (w, c) =>
        {
            var list = C<ListBox>(w, "ImageList");
            var before = Directory.GetFiles(c.Root).ToDictionary(path => path,
                path => (Hash: SafePaths.Hash(path), Modified: File.GetLastWriteTimeUtc(path)));
            await Arrow(w, "CombinedTextBox", Key.Up); Assert(list.SelectedIndex == 0, "첫 신청서 이전 이동");
            foreach (var source in new[] { "CombinedTextBox", "ImageList", "ImageStage" })
            {
                var previous = list.SelectedIndex; await Arrow(w, source, Key.Down);
                Assert(list.SelectedIndex == previous + 1, "다음 신청서 한 장 이동 실패: " + source);
            }
            await Arrow(w, "ImageList", Key.Down); Assert(list.SelectedIndex == 3, "끝 신청서 이후 이동");
            foreach (var source in new[] { "ImageStage", "ImageList", "CombinedTextBox" })
            {
                var previous = list.SelectedIndex; await Arrow(w, source, Key.Up);
                Assert(list.SelectedIndex == previous - 1, "이전 신청서 한 장 이동 실패: " + source);
            }
            Assert(before.All(pair => SafePaths.Hash(pair.Key) == pair.Value.Hash && File.GetLastWriteTimeUtc(pair.Key) == pair.Value.Modified), "방향키 탐색이 원본을 변경함");
            Assert(!Directory.Exists(Path.Combine(c.Root, ".rmslite")), "단순 탐색이 저장 기록을 생성함");
        }));

        await check("AR02 방향키는 이름 저장 후 한 장 이동", () => InWindow(root, "save-name", async (w, c) =>
        {
            var list = C<ListBox>(w, "ImageList"); var input = C<TextBox>(w, "CombinedTextBox");
            input.Text = "1가상수정가"; await Arrow(w, "CombinedTextBox", Key.Down);
            Assert(File.Exists(Path.Combine(c.Root, "1가상수정가.png")) && !File.Exists(Path.Combine(c.Root, "1가상자료.png")), "아래키 이름 저장 누락");
            Assert(((ImageItem)list.SelectedItem).Quota == "2", "이름 저장 후 다음 대상 오류");
            input.Text = "2가상수정나"; await Arrow(w, "ImageList", Key.Up);
            Assert(File.Exists(Path.Combine(c.Root, "2가상수정나.png")) && ((ImageItem)list.SelectedItem).Name == "가상수정가", "위키 저장 또는 이전 선택 누락");
        }));

        await check("AR03 잘못된·중복 이름은 이동 차단, 빈 이름은 원래대로 이동", () => InWindow(root, "invalid-name", async (w, c) =>
        {
            var list = C<ListBox>(w, "ImageList"); var input = C<TextBox>(w, "CombinedTextBox");
            foreach (var invalid in new[] { "1잘못/이름", "2가상자료" })
            {
                input.Text = invalid; await Arrow(w, "CombinedTextBox", Key.Down);
                Assert(list.SelectedIndex == 0 && w.HasPendingRename && input.Text == invalid, "실패한 이름을 버리고 이동: " + invalid);
            }
            input.Clear(); await Arrow(w, "CombinedTextBox", Key.Down);
            Assert(list.SelectedIndex == 1 && File.Exists(Path.Combine(c.Root, "1가상자료.png")), "빈 이름 다음 이동 또는 원본 보존 실패");
            input.Clear(); await Arrow(w, "CombinedTextBox", Key.Up);
            Assert(list.SelectedIndex == 0 && input.Text == "1가상자료" && Directory.GetFiles(c.Root, "*.png").Length == 4, "빈 이름 이전 이동 실패");
        }));

        await check("AR04 카드 저장 대기 및 읽기 전용 실패 시 현재 신청서 유지", () => InWindow(root, "status", async (w, c) =>
        {
            var list = C<ListBox>(w, "ImageList");
            Click(C<CheckBox>(w, "CardCheck"), true); await Arrow(w, "CombinedTextBox", Key.Down);
            Assert(list.SelectedIndex == 1 && LiteWorkspace.Load(c).Single(item => item.Quota == "1").Card, "방향키가 선행 카드 저장을 누락함");
            var path = ((ImageItem)list.SelectedItem).FullPath; File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                Click(C<CheckBox>(w, "CardCheck"), true); await w.LastAutoSaveTask.WaitAsync(Timeout);
                await Arrow(w, "CombinedTextBox", Key.Down);
                Assert(list.SelectedIndex == 1 && w.HasPendingStatus && C<CheckBox>(w, "CardCheck").IsChecked == true, "상태 저장 실패 후 초안을 버리고 이동함");
            }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
            await Arrow(w, "CombinedTextBox", Key.Down);
            Assert(list.SelectedIndex == 2 && LiteWorkspace.Load(c).Single(item => item.Quota == "2").Card, "파일 복구 후 상태 저장/방향키 재시도 실패");
        }));

        await check("AR05 검색·메모·콤보·체크·버튼 방향키 보호", () => InWindow(root, "control-scope", async (w, _) =>
        {
            await Arrow(w, "CombinedTextBox", Key.Down); var selected = C<ListBox>(w, "ImageList").SelectedItem;
            foreach (var name in new[] { "SearchText", "RepairMemo", "StatusMemo", "StatusFilter", "CardCheck", "CopyApplicantNameButton" })
            {
                var task = w.LastShortcutTask;
                Raise(C<UIElement>(w, name), Key.Up); Raise(C<UIElement>(w, name), Key.Down);
                Assert(ReferenceEquals(task, w.LastShortcutTask), "일반 입력 컨트롤의 방향키 가로챔: " + name);
                Assert(ReferenceEquals(selected, C<ListBox>(w, "ImageList").SelectedItem), "다른 입력 중 신청서 이동: " + name);
            }
        }));

        await check("AR06 Ctrl·Shift·Alt 조합과 IME 후보 방향키 보호", () => InWindow(root, "modifier-ime", async (w, _) =>
        {
            await Arrow(w, "CombinedTextBox", Key.Down);
            foreach (var modifier in new[] { ModifierKeys.Control, ModifierKeys.Shift, ModifierKeys.Alt, ModifierKeys.Control | ModifierKeys.Shift })
            {
                w.ModifiersForTests = () => modifier; var task = w.LastShortcutTask;
                foreach (var source in new[] { "CombinedTextBox", "ImageList", "ImageStage" })
                    foreach (var key in new[] { Key.Up, Key.Down }) Raise(C<UIElement>(w, source), key);
                Assert(ReferenceEquals(task, w.LastShortcutTask) && C<ListBox>(w, "ImageList").SelectedIndex == 1, "수정키 조합을 앱 탐색으로 처리: " + modifier);
            }
            w.ModifiersForTests = () => ModifierKeys.None;
            foreach (var key in new[] { Key.Up, Key.Down })
            {
                var task = w.LastShortcutTask;
                var down = Raise(C<TextBox>(w, "CombinedTextBox"), key, ime: true);
                Raise(C<TextBox>(w, "CombinedTextBox"), key, ime: true, routedEvent: Keyboard.PreviewKeyUpEvent);
                Assert(down.Key == Key.ImeProcessed && down.ImeProcessedKey == key, "IME 합성 이벤트 오류");
                Assert(ReferenceEquals(task, w.LastShortcutTask) && C<ListBox>(w, "ImageList").SelectedIndex == 1, "IME 후보 이동 중 신청서 이동");
            }
        }));

        await check("AR07 키 반복은 완료 후 이동, 처리 중 중복 키는 누적하지 않음", () => InWindow(root, "repeat-busy", async (w, _) =>
        {
            var source = C<TextBox>(w, "CombinedTextBox"); var list = C<ListBox>(w, "ImageList");
            var first = Raise(source, Key.Down); var moving = w.LastShortcutTask;
            Assert(first.Handled && !moving.IsCompleted, "이동 대기 테스트가 비동기 경로를 거치지 않음");
            for (var index = 0; index < 4; index++) Assert(Raise(source, Key.Down, repeat: true).Handled, "이동 중 반복키가 기본 컨트롤로 전달됨");
            await moving.WaitAsync(Timeout); await w.LastPreviewTask.WaitAsync(Timeout);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(list.SelectedIndex == 1, "처리 중 키가 누적되어 여러 신청서를 건너뜀");
            await Arrow(w, "CombinedTextBox", Key.Down, repeat: true); Assert(list.SelectedIndex == 2, "완료 후 키 반복 이동 누락");
            await Arrow(w, "ImageList", Key.Up, repeat: true); Assert(list.SelectedIndex == 1, "완료 후 위키 반복 이동 누락");
        }));

        await check("AR08 분리된 이름·목록·이미지 창도 동일한 위아래 이동", () => InWindow(root, "detached", async (w, _) =>
        {
            foreach (var panel in new[] { (Key: "rename", Host: "LeftHost", Source: "CombinedTextBox"), (Key: "list", Host: "RightHost", Source: "ImageList"), (Key: "image", Host: "ImageHost", Source: "ImageStage") })
            {
                w.ToggleDock(panel.Key, C<ContentControl>(w, panel.Host), "방향키 테스트", 360);
                await Dispatcher.Yield(DispatcherPriority.Loaded); w.UpdateLayout();
                var source = C<UIElement>(w, panel.Source); var host = Window.GetWindow(source);
                Assert(host is PanelWindow && host != w && !host.ShowActivated, "분리창 테스트 환경 오류");
                await Arrow(w, panel.Source, Key.Down); Assert(C<ListBox>(w, "ImageList").SelectedIndex == 1, "분리창 아래키 미작동: " + panel.Key);
                await Arrow(w, panel.Source, Key.Up); Assert(C<ListBox>(w, "ImageList").SelectedIndex == 0, "분리창 위키 미작동: " + panel.Key);
                host.Close(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            }
        }));
    }
}
