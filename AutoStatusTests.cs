using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
namespace BoramRms.Lite;

public static class AutoStatusTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);
    private static void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static T Control<T>(MainWindow w, string name) where T : class => w.FindName(name) as T ?? throw new InvalidOperationException("컨트롤 없음: " + name);
    private static FolderContext Fixture(string root, string name, int count = 2)
    {
        var path = Path.Combine(root, name); Directory.CreateDirectory(path);
        for (int n = 1; n <= count; n++) SelfTest.AddImage(path, n + "가상자료.png");
        return FolderContext.Resolve(path);
    }
    private static ImageItem Saved(FolderContext c, string quota = "1") => LiteWorkspace.Load(c).Single(i => i.Quota == quota);
    private static void Click(CheckBox box, bool value) { box.IsChecked = value; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    private static void Click(MainWindow w, string name, bool value) => Click(Control<CheckBox>(w, name), value);
    private static CheckBox Repair(MainWindow w, string reason) => Control<UniformGrid>(w, "RepairReasons").Children.OfType<CheckBox>().Single(c => c.Content.ToString() == reason);
    private static async Task InWindow(FolderContext context, Func<MainWindow, Task> action)
    {
        var w = new MainWindow(testMode: true) { Left = -16000, Top = -16000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Show();
        try { await w.OpenFolderAsync(context.Root); await w.LastPreviewTask; w.UpdateLayout(); await action(w).WaitAsync(Timeout); }
        finally
        {
            w.ModifiersForTests = null; w.AutoSaveWriterForTests = null;
            await w.FlushStatusSavesAsync().WaitAsync(Timeout);
            if (w.IsVisible) { await w.ReloadAsync(); await w.LastPreviewTask; w.Close(); }
        }
    }
    private static async Task Space(MainWindow w, bool control)
    {
        w.ModifiersForTests = () => control ? ModifierKeys.Control : ModifierKeys.None;
        try
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Control<TextBox>(w, "CombinedTextBox").RaiseEvent(args);
            Assert(args.Handled, "Space 키 이벤트를 처리하지 않음");
            await w.LastShortcutTask.WaitAsync(Timeout); await w.LastPreviewTask.WaitAsync(Timeout);
        }
        finally { w.ModifiersForTests = null; }
    }
    public static async Task RunAsync(string run, Action<string, Action> check, Func<string, Func<Task>, Task> checkAsync)
    {
        var root = Path.Combine(run, "instant-status"); Directory.CreateDirectory(root);
        await checkAsync("A01 보완 사유 체크·해제 즉시 파일 저장 및 다시 열기", async () =>
        {
            var c = Fixture(root, "repairs");
            await InWindow(c, async w =>
            {
                Click(Repair(w, "화산미기입"), true); await w.LastAutoSaveTask;
                Assert(Saved(c).Selections?.Repairs == "화산미기입", "수동 저장 없이 첫 보완 사유 미저장");
                Click(Repair(w, "남녀구분미체크"), true); await w.LastAutoSaveTask;
                Assert(Saved(c).Selections!.Repairs.Contains("화산미기입") && Saved(c).Selections!.Repairs.Contains("남녀구분미체크"), "복수 보완 사유 손실");
                Click(Repair(w, "화산미기입"), false); await w.LastAutoSaveTask;
                await w.ReloadAsync(); await w.LastPreviewTask;
                Assert(Repair(w, "화산미기입").IsChecked == false && Repair(w, "남녀구분미체크").IsChecked == true, "해제/재읽기 실패");
            });
        });
        var flags = new[] { ("CardCheck", "카드"), ("WeekendCheck", "주말미등록"), ("AdditionalCheck", "추가"), ("PreCancelCheck", "입력전 취소"), ("PostCancelCheck", "입력후 취소"), ("MinorCheck", "미성년자") };
        for (int n = 0; n < flags.Length; n++)
        {
            var (name, label) = flags[n]; var id = n + 2;
            await checkAsync($"A{id:00} {label} 체크·해제 즉시 저장", async () =>
            {
                var c = Fixture(root, "flag-" + name);
                await InWindow(c, async w =>
                {
                    var file = Saved(c).FullPath; var hash = SafePaths.Hash(file);
                    Click(w, name, true); await w.LastAutoSaveTask;
                    Assert(name == "CardCheck" ? Saved(c).Card : Saved(c).Status.Contains(label), "즉시 저장 실패: " + label);
                    Assert(SafePaths.Hash(file) == hash && Control<ListBox>(w, "ImageList").SelectedIndex == 0, "체크가 원본/선택을 변경함");
                    Click(w, name, false); await w.LastAutoSaveTask;
                    Assert(name == "CardCheck" ? !Saved(c).Card : !Saved(c).Status.Contains(label), "체크 해제 미저장: " + label);
                    Assert(!w.HasPendingStatus, "완료 후 미저장 상태가 남음");
                });
            });
        }
        await checkAsync("A08 빠른 체크·해제 순서를 보존하고 최종 상태 저장", async () =>
        {
            var c = Fixture(root, "rapid");
            await InWindow(c, async w =>
            {
                for (int n = 0; n < 15; n++) Click(w, "CardCheck", n % 2 == 0);
                Click(Repair(w, "주소보완"), true); Click(w, "MinorCheck", true);
                await w.FlushStatusSavesAsync(); var item = Saved(c);
                Assert(item.Card && item.Status.Contains("미성년자") && item.Selections!.Repairs.Contains("주소보완"), "순서/최종 체크 손실");
                Assert(!w.HasPendingStatus && Control<FrameworkElement>(w, "WorkArea").IsEnabled, "저장 후 UI 잠금");
            });
        });
        await checkAsync("A09 Space 다음·Ctrl+Space 이전 실제 키 이벤트와 이름 저장", async () =>
        {
            var c = Fixture(root, "key-directions", 3);
            await InWindow(c, async w =>
            {
                Control<TextBox>(w, "CombinedTextBox").Text = "4가상앞"; await Space(w, false);
                Assert(File.Exists(Path.Combine(c.Root, "4가상앞.png")) && ((ImageItem)Control<ListBox>(w, "ImageList").SelectedItem).Quota == "2", "Space 저장/다음 실패");
                Control<TextBox>(w, "CombinedTextBox").Text = "5가상뒤"; await Space(w, true);
                Assert(File.Exists(Path.Combine(c.Root, "5가상뒤.png")) && ((ImageItem)Control<ListBox>(w, "ImageList").SelectedItem).Quota == "4", "Ctrl+Space 저장/이전 실패");
            });
        });
        await checkAsync("A10 체크 저장 중 다음 이동은 완료를 기다리고 다른 이미지에 적용하지 않음", async () =>
        {
            var c = Fixture(root, "save-before-next");
            await InWindow(c, async w =>
            {
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                w.AutoSaveWriterForTests = async (ctx, item, state) => { await gate.Task; return LiteWorkspace.Save(ctx, item, null, state); };
                Task navigation = Task.CompletedTask;
                try
                {
                    Click(w, "CardCheck", true); navigation = w.NavigateImageAsync(1);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert(!navigation.IsCompleted && Control<ListBox>(w, "ImageList").SelectedIndex == 0, "상태 기록 전에 이동함");
                }
                finally { gate.TrySetResult(true); await navigation.WaitAsync(Timeout); }
                Assert(Saved(c).Card && !Saved(c, "2").Card && Control<ListBox>(w, "ImageList").SelectedIndex == 1, "상태가 다음 이미지로 새어 나감");
            });
        });
        await checkAsync("A11 체크 저장은 작성 중 이름·커서·이미지 확대·이동을 보존", async () =>
        {
            var c = Fixture(root, "draft-preserved");
            await InWindow(c, async w =>
            {
                var input = Control<TextBox>(w, "CombinedTextBox"); input.Text = "아직쓰는이름"; input.CaretIndex = 3;
                w.SetViewport(2.77, 31, -90); var bitmap = Control<Image>(w, "PreviewImage").Source;
                Click(Repair(w, "계약일보완"), true); await w.LastAutoSaveTask;
                Assert(input.Text == "아직쓰는이름" && input.CaretIndex == 3 && w.HasPendingRename, "체크가 이름 입력을 초기화함");
                Assert(ReferenceEquals(bitmap, Control<Image>(w, "PreviewImage").Source) && Control<ScaleTransform>(w, "ImageScale").ScaleX == 2.77 && Control<TranslateTransform>(w, "ImageTranslate").Y == -90, "체크 저장이 이미지 보기를 변경함");
                Assert(Saved(c).Selections!.Repairs.Contains("계약일보완"), "미완성 이름 때문에 상태 저장이 막힘");
            });
        });
        await checkAsync("A12 상태 저장 실패는 체크·현재 선택 보존, 재시도 성공", async () =>
        {
            var c = Fixture(root, "failure-retry");
            await InWindow(c, async w =>
            {
                var file = Saved(c).FullPath; File.SetAttributes(file, FileAttributes.ReadOnly);
                try
                {
                    Click(w, "CardCheck", true); Assert(!await w.LastAutoSaveTask, "읽기 전용 상태를 성공으로 표시함");
                    await w.NavigateImageAsync(1);
                    Assert(Control<ListBox>(w, "ImageList").SelectedIndex == 0 && Control<CheckBox>(w, "CardCheck").IsChecked == true && w.HasPendingStatus, "저장 실패 후 선택/입력이 사라짐");
                    Assert(Control<TextBlock>(w, "StatusText").Text.Contains(Path.GetFileName(file)), "어느 파일 실패인지 표시하지 않음");
                }
                finally { File.SetAttributes(file, FileAttributes.Normal); }
                Control<Button>(w, "SaveStateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await w.LastAutoSaveTask;
                Assert(Saved(c).Card && !w.HasPendingStatus, "재시도 실패");
            });
        });
        await checkAsync("A13 체크 직후 탭 전환은 원래 폴더에 저장 후 전환", async () =>
        {
            var a = Fixture(root, "tab-a"); var b = Fixture(root, "tab-b");
            await InWindow(a, async w =>
            {
                await w.OpenFolderAsync(b.Root); await w.LastPreviewTask; await w.OpenFolderAsync(a.Root); await w.LastPreviewTask;
                Click(w, "AdditionalCheck", true);
                Control<ListBox>(w, "WorkTabs").SelectedItem = w.Tabs.Single(t => SafePaths.Same(t.Context.Root, b.Root));
                await w.LastNavigationTask; await w.LastPreviewTask;
                Assert(Saved(a).Status.Contains("추가") && Saved(b).Status == "" && SafePaths.Under(w.AllItems[0].FullPath, b.Root), "탭 전환 상태 혼합");
            });
        });
        await checkAsync("A14 목록 클릭 이동도 현재 이름을 저장하고 팝업 없이 전환", async () =>
        {
            var c = Fixture(root, "list-save");
            await InWindow(c, async w =>
            {
                var input = Control<TextBox>(w, "CombinedTextBox"); input.Text = "3가상수정";
                var list = Control<ListBox>(w, "ImageList"); list.SelectedItem = w.AllItems.Single(i => i.Quota == "2");
                await w.LastNavigationTask; await w.LastPreviewTask;
                Assert(File.Exists(Path.Combine(c.Root, "3가상수정.png")) && ((ImageItem)list.SelectedItem).Quota == "2" && !w.HasPendingRename, "목록 이동 전에 저장 안 됨");
                Assert(typeof(MainWindow).GetMethod("ConfirmDiscard", BindingFlags.Instance | BindingFlags.NonPublic) == null, "입력 폐기 팝업 경로가 남음");
            });
        });
        await checkAsync("A15 메모 입력 완료 시 자동 저장하고 공백 입력은 보호", async () =>
        {
            var c = Fixture(root, "memo-focus");
            await InWindow(c, async w =>
            {
                var memo = Control<TextBox>(w, "RepairMemo"); memo.Text = "주소 확인 필요";
                memo.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, memo, Control<TextBox>(w, "CombinedTextBox")) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                await w.LastAutoSaveTask;
                Assert(Saved(c).Selections?.RepairMemo == "주소 확인 필요", "메모 자동 저장 실패");
                Assert(!w.CanHandleSpace(memo) && !w.CanHandleSpace(Control<TextBox>(w, "SearchText")), "메모·검색의 공백을 이동으로 가로챔");
            });
        });
        await checkAsync("A16 미저장 이름 취소는 이미 자동 저장된 체크를 취소하지 않음", async () =>
        {
            var c = Fixture(root, "discard-only-draft");
            await InWindow(c, async w =>
            {
                Click(w, "CardCheck", true); await w.LastAutoSaveTask;
                Control<TextBox>(w, "CombinedTextBox").Text = "아직저장안한이름";
                await w.DiscardPendingInputAsync();
                Assert(!w.HasPendingRename && Saved(c).Card && Control<CheckBox>(w, "CardCheck").IsChecked == true && Control<TextBox>(w, "CombinedTextBox").Text == "1가상자료", "이미 저장된 상태를 미저장 취소로 변경함");
            });
        });
        await checkAsync("A17 마지막 변경 취소에 대상 설명·실제 한 단계 체크 취소", async () =>
        {
            var c = Fixture(root, "undo-explained");
            await InWindow(c, async w =>
            {
                var undo = Control<Button>(w, "UndoLastButton"); Assert(!undo.IsEnabled, "없는 이력 취소 버튼 활성화");
                Click(w, "CardCheck", true); await w.LastAutoSaveTask;
                Assert(undo.IsEnabled && undo.ToolTip.ToString()!.Contains("1가상자료.png") && undo.ToolTip.ToString()!.Contains("체크"), "대상/작업 설명 누락");
                await w.UndoDraftAsync();
                Assert(!Saved(c).Card && !undo.IsEnabled, "마지막 체크 취소 실패");
            });
        });
        await checkAsync("A18 이전 폴더 열기 설명 및 파일을 수정하지 않는 재개", async () =>
        {
            var c = Fixture(root, "restore-explained"); var file = Saved(c, "2").FullPath;
            SettingsStore.Save(new SavedSettings { Folders = new() { c.Root }, Selected = new() { [c.Root] = file } });
            var before = Directory.GetFiles(c.Root).ToDictionary(p => p, SafePaths.Hash);
            await InWindow(c, async w =>
            {
                var restore = Control<Button>(w, "RestoreFoldersButton");
                Assert(restore.Content.ToString() == "이전 폴더 열기" && restore.ToolTip.ToString()!.Contains("파일 내용은 되돌리지"), "폴더 복원 설명 불명확");
                await w.RestorePreviousFoldersAsync();
                Assert(((ImageItem)Control<ListBox>(w, "ImageList").SelectedItem).FullPath == file && before.All(p => SafePaths.Hash(p.Key) == p.Value), "폴더 재개가 데이터 복구를 실행함");
                Assert(MainWindow.WorkflowHelpText.Contains("Ctrl+Space") && MainWindow.WorkflowHelpText.Contains("파일 삭제·300KB 압축·업데이트는 취소할 수 없"), "사용 안내 누락");
            });
        });
        await checkAsync("A19 보완·카드·추가·미성년자 조합 보존과 취소 상태 상호 전환", async () =>
        {
            var c = Fixture(root, "combined-flags");
            await InWindow(c, async w =>
            {
                Click(Repair(w, "화산미기입"), true); Click(w, "CardCheck", true); Click(w, "AdditionalCheck", true); Click(w, "MinorCheck", true);
                Click(w, "PreCancelCheck", true); Click(w, "PostCancelCheck", true); await w.FlushStatusSavesAsync();
                await w.ReloadAsync(); await w.LastPreviewTask;
                Assert(Repair(w, "화산미기입").IsChecked == true && Control<CheckBox>(w, "CardCheck").IsChecked == true && Control<CheckBox>(w, "AdditionalCheck").IsChecked == true && Control<CheckBox>(w, "MinorCheck").IsChecked == true, "호환되는 선택이 사라짐");
                Assert(Control<CheckBox>(w, "PreCancelCheck").IsChecked == false && Control<CheckBox>(w, "PostCancelCheck").IsChecked == true, "입력전/입력후 취소 동시 선택");
                SelfTest.Render(w, Path.Combine(run, "autosave-preview.png"), 1424, 941);
            });
        });
        await checkAsync("A20 처음·마지막에서도 저장은 수행, 양방향 이동 경계 유지", async () =>
        {
            var c = Fixture(root, "boundary", 1);
            await InWindow(c, async w =>
            {
                Control<TextBox>(w, "CombinedTextBox").Text = "2가상경계"; await Space(w, true);
                Assert(File.Exists(Path.Combine(c.Root, "2가상경계.png")) && Control<ListBox>(w, "ImageList").SelectedIndex == 0, "처음에서 역방향 저장 실패");
                await Space(w, false); Assert(Control<ListBox>(w, "ImageList").SelectedIndex == 0, "마지막에서 순환 이동함");
                Assert(MainWindow.SpaceDirection(ModifierKeys.Shift) == 0 && MainWindow.SpaceDirection(ModifierKeys.Alt) == 0, "다른 단축키 조합 침범");
            });
        });
        await checkAsync("A21 자동 상태 저장이 목록 다중 선택을 유지", async () =>
        {
            var c = Fixture(root, "multi-select", 3);
            await InWindow(c, async w =>
            {
                var list = Control<ListBox>(w, "ImageList"); list.SelectedItems.Add(list.Items[1]);
                Assert(list.SelectedItems.Count == 2, "다중 선택 준비 실패");
                Click(w, "CardCheck", true); await w.LastAutoSaveTask;
                Assert(list.SelectedItems.Count == 2 && Saved(c).Card && !Saved(c, "2").Card, "체크 저장이 다중 선택 또는 다른 이미지 변경");
            });
        });
        await checkAsync("A22 동일 이름 입력은 미수정 처리하고 체크만으로 폐기 팝업 없음", async () =>
        {
            var c = Fixture(root, "actual-dirty");
            await InWindow(c, async w =>
            {
                var input = Control<TextBox>(w, "CombinedTextBox"); input.Text = "2잠깐수정"; input.Text = "1가상자료";
                Assert(!w.HasPendingRename, "원상복귀한 이름도 미저장으로 오판");
                Click(Repair(w, "남녀구분미체크"), true); await w.LastAutoSaveTask;
                await w.NavigateImageAsync(1);
                Assert(Control<ListBox>(w, "ImageList").SelectedIndex == 1 && !w.HasPendingStatus && typeof(MainWindow).GetMethod("ConfirmOtherInput", BindingFlags.Instance | BindingFlags.NonPublic) == null, "입력 확인 경로 잔존");
            });
        });
        await checkAsync("A23 체크 직후 창 닫기는 저장을 마친 뒤 닫기", async () =>
        {
            var c = Fixture(root, "close-after-save", 1);
            await InWindow(c, async w =>
            {
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                w.AutoSaveWriterForTests = async (ctx, item, state) => { await gate.Task; return LiteWorkspace.Save(ctx, item, null, state); };
                try { Click(w, "CardCheck", true); w.Close(); Assert(w.IsVisible, "저장 중 창이 먼저 닫힘"); }
                finally { gate.TrySetResult(true); }
                await w.FlushStatusSavesAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(Saved(c).Card && !w.IsVisible, "종료 전 체크 저장 또는 종료 실패");
            });
        });
        await checkAsync("A24 상태 모두 해제는 이름 변경 없이 즉시 저장", async () =>
        {
            var c = Fixture(root, "clear-all");
            await InWindow(c, async w =>
            {
                Click(Repair(w, "주소보완"), true); Click(w, "CardCheck", true); Click(w, "MinorCheck", true); await w.FlushStatusSavesAsync();
                Control<Button>(w, "ClearAllStateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await w.LastAutoSaveTask;
                var item = Saved(c); Assert(!item.Card && item.Status == "" && item.Details == "" && item.FileName == "1가상자료.png", "전체 해제 또는 파일명 보존 실패");
            });
        });
    }
}
