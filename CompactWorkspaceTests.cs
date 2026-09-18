using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace BoramRms.Lite;

public static class CompactWorkspaceTests
{
    private static T C<T>(MainWindow w, string name) where T : class => (T)w.FindName(name);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Paint(Window w) { w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); w.UpdateLayout(); }
    private static void Click(CheckBox box, bool state) { box.IsChecked = state; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    private static string Fixture(string root, string name)
    {
        var folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        for (int n = 1; n <= 12; n++) SelfTest.AddImage(folder, n + "가상자료.png");
        return folder;
    }
    private static async Task Window(string root, string name, Func<MainWindow, string, Task> action)
    {
        var folder = Fixture(root, name);
        var w = new MainWindow(true) { Left = -16000, Top = -16000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Show();
        try { await w.OpenFolderAsync(folder); await w.LastPreviewTask; await Paint(w); await action(w, folder).WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { w.ModifiersForTests = null; w.AutoSaveWriterForTests = null; await w.FlushStatusSavesAsync(); await w.ReloadAsync(); await w.LastPreviewTask; w.Close(); }
    }
    private static Rect Bounds(FrameworkElement element, Visual relative) => element.TransformToAncestor(relative).TransformBounds(new Rect(element.RenderSize));
    private static void Inside(FrameworkElement element, Window window)
    {
        var bounds = Bounds(element, window);
        Assert(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1 && bounds.Width > 0 && bounds.Height > 0, "창 밖의 컨트롤: " + element.Name + " " + bounds);
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }
    public static async Task RunAsync(string run, Action<string, Action> check, Func<string, Func<Task>, Task> checkAsync)
    {
        var root = Path.Combine(run, "compact-workspace"); Directory.CreateDirectory(root);
        var profile = SettingsStore.DirectoryPath; SettingsStore.DirectoryPath = Path.Combine(root, "profile");
        InterfaceScale.EnsureLoaded(true); ThemeManager.EnsureLoaded(true);
        try
        {
            check("D01 기존 RMS와 동일한 5개 배율·기본 90%", () =>
            {
                Assert(InterfaceScale.Options.Select(p => p.Percent).SequenceEqual(new[] {80,85,90,95,100}) && InterfaceScale.Percent == 90, "배율 목록/기본값 불일치");
            });
            await checkAsync("D02 배율 메뉴 전환·입력·원본 보존", () => Window(root, "scale", async (w, folder) =>
            {
                var input = C<TextBox>(w, "CombinedTextBox"); input.Text = "작성중"; input.Select(1, 1);
                var image = C<Image>(w, "PreviewImage").Source; var selected = C<ListBox>(w, "ImageList").SelectedItem;
                var hashes = Directory.GetFiles(folder).ToDictionary(p => p, SafePaths.Hash);
                foreach (var option in InterfaceScale.Options)
                {
                    C<ComboBox>(w, "UiScaleComboBox").SelectedItem = option; await Paint(w);
                    Assert(InterfaceScale.Percent == option.Percent && Math.Abs(C<Grid>(w, "AppLayout").LayoutTransform.Value.M11 - option.Percent / 100.0) < .001, "배율 미적용");
                    Assert(input.Text == "작성중" && input.SelectionStart == 1 && input.SelectionLength == 1 && ReferenceEquals(image, C<Image>(w, "PreviewImage").Source) && ReferenceEquals(selected, C<ListBox>(w, "ImageList").SelectedItem), "입력/선택 초기화");
                }
                Assert(hashes.All(p => SafePaths.Hash(p.Key) == p.Value), "배율 변경이 이미지 수정");
            }));
            check("D03 배율 저장 복원과 테마·작업 설정 분리", () =>
            {
                ThemeManager.Select("purple"); SettingsStore.Save(new SavedSettings());
                var theme = SafePaths.Hash(ThemeManager.PreferencePath); var settings = SafePaths.Hash(Path.Combine(SettingsStore.DirectoryPath, "settings.json"));
                InterfaceScale.Select(85); InterfaceScale.Select(100, false); InterfaceScale.EnsureLoaded(true);
                Assert(InterfaceScale.Percent == 85 && SafePaths.Hash(ThemeManager.PreferencePath) == theme && SafePaths.Hash(Path.Combine(SettingsStore.DirectoryPath, "settings.json")) == settings, "배율 복원/설정 분리 실패");
                var next = new MainWindow(true); try { Assert(((ScaleOption)C<ComboBox>(next, "UiScaleComboBox").SelectedItem).Percent == 85, "새 창 복원 실패"); } finally { next.Close(); }
            });
            await checkAsync("D04 1024x720에서 5개 배율의 하단·입력·목록 접근", () => Window(root, "small", async (w, folder) =>
            {
                foreach (var scale in InterfaceScale.Options)
                {
                    w.ChangeInterfaceScale(scale.Percent); w.Width = 1024; w.Height = 720; await Paint(w);
                    Inside(C<ComboBox>(w, "UiScaleComboBox"), w); Inside(C<Button>(w, "CompressOriginalsButton"), w); Inside(C<Button>(w, "RecycleSelectedButton"), w); Inside(C<ComboBox>(w, "FolderSelector"), w);
                    var scroll = C<ScrollViewer>(w, "RenameScroller"); scroll.ScrollToEnd(); await Paint(w);
                    Inside(C<Button>(w, "ClearAllStateButton"), w);
                    var clear = Bounds(C<Button>(w, "ClearAllStateButton"), scroll);
                    Assert(clear.Top >= 0 && clear.Bottom <= scroll.ActualHeight + 2, "스크롤 후 마지막 항목이 가려짐");
                    scroll.ScrollToTop(); await Paint(w); Inside(C<TextBox>(w, "CombinedTextBox"), w);
                    Assert(C<ListBox>(w, "ImageList").ActualHeight > 100, "이미지 목록 표시 공간 부족");
                }
                w.ChangeInterfaceScale(85); w.Width = 1024; w.Height = 720; await Paint(w);
                Capture(w, Path.Combine(run, "compact-1024.png"));
            }));
            await checkAsync("D05 화면 배율 변경 후 전체 이미지 맞춤", () => Window(root, "image-fit", async (w, folder) =>
            {
                w.ChangeInterfaceScale(100); await Paint(w); w.SetViewport(2.7, 40, -60);
                w.ChangeInterfaceScale(80); await Paint(w);
                var stage = C<Border>(w, "ImageStage"); var image = C<Image>(w, "PreviewImage"); var box = Bounds(image, stage);
                Assert(C<ScaleTransform>(w, "ImageScale").ScaleX == 1 && box.Left >= -1 && box.Top >= -1 && box.Right <= stage.ActualWidth + 1 && box.Bottom <= stage.ActualHeight + 1, "배율 변경 후 이미지 일부만 표시");
            }));
            await checkAsync("D06 줄인 체크·목록 간격과 이름 글자 잘림 방지", () => Window(root, "density", async (w, folder) =>
            {
                w.ChangeInterfaceScale(100); await Paint(w);
                var box = C<TextBox>(w, "CombinedTextBox"); box.ApplyTemplate();
                var host = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
                var list = C<ListBox>(w, "ImageList"); var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                Assert(box.ActualHeight >= 62 && host.ScrollableHeight < .5, "이름 입력 잘림 재발");
                Assert(row.ActualHeight <= 32 && C<CheckBox>(w, "CardCheck").ActualHeight <= 22, "목록/체크 간격이 줄지 않음");
                var input = box.GetRectFromCharacterIndex(0); Assert(!input.IsEmpty && input.Top >= 0 && input.Bottom <= box.ActualHeight, "글자 경계 오류");
                w.Width = 1280; w.Height = 900; w.ChangeInterfaceScale(90); await Paint(w); Capture(w, Path.Combine(run, "compact-main.png"));
            }));
            await checkAsync("D07 분리창·안내·업데이트창 배율 동기화", () => Window(root, "aux", async (w, folder) =>
            {
                var panel = new PanelWindow("시험", new Button { Content = "시험" }, 350); var help = new GuideWindow(); var update = new UpdateWindow(true);
                try
                {
                    foreach (var x in new Window[] {panel,help,update}) { x.WindowStartupLocation = WindowStartupLocation.Manual; x.Left = -16000; x.Top = -16000; x.ShowInTaskbar = false; x.Show(); }
                    foreach (var p in new[] {80,100})
                    {
                        w.ChangeInterfaceScale(p); await Paint(w);
                        foreach (var x in new Window[] {panel,help,update}) Assert(Math.Abs(((FrameworkElement)x.Content).LayoutTransform.Value.M11 - p / 100.0) < .001, "보조 창 배율 미동기화");
                    }
                }
                finally { panel.Close(); help.Close(); update.Close(); }
            }));
            check("D08 손상된 배율 설정의 기본값·허용 범위", () =>
            {
                File.WriteAllText(InterfaceScale.PreferencePath, "{bad json"); InterfaceScale.EnsureLoaded(true); Assert(InterfaceScale.Percent == 90, "설정 기본값 실패");
                bool rejected = false; try { InterfaceScale.Select(10); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Assert(rejected && InterfaceScale.Percent == 90, "잘못된 배율 허용"); InterfaceScale.Select(90);
            });
            await checkAsync("D09 사용 안내 4분류·단계 카드·실제 창 연결", () => Window(root, "guide", async (w, folder) =>
            {
                C<Button>(w, "WorkflowHelpButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var guide = w.OwnedWindows.OfType<GuideWindow>().Single(); guide.Left = -16000; guide.Top = -16000; guide.ShowInTaskbar = false;
                try
                {
                    for (int i = 0; i < 4; i++) { guide.ShowTopic(i); await Paint(guide); Assert(guide.TopicIndex == i && guide.Body.Children.OfType<Border>().Count() >= 4, "도움말 카드/분류 누락"); }
                    guide.ShowTopic(0); await Paint(guide); Capture(guide, Path.Combine(run, "guide-start.png"));
                    guide.ShowTopic(1); await Paint(guide); Capture(guide, Path.Combine(run, "guide-shortcuts.png"));
                    C<Button>(w, "WorkflowHelpButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(w.OwnedWindows.OfType<GuideWindow>().Count() == 1, "안내 창 중복 생성");
                }
                finally { guide.Close(); }
            }));
            await checkAsync("D10 좁은 도움말 창의 줄바꿈·세로 스크롤", async () =>
            {
                var guide = new GuideWindow { Left = -16000, Top = -16000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Width = 520, Height = 420 };
                guide.Show(); try { guide.ShowTopic(3); await Paint(guide); Assert(guide.Scroller.ScrollableHeight > 0 && guide.Scroller.ComputedHorizontalScrollBarVisibility != Visibility.Visible, "좁은 창 안내 접근 불가"); guide.Scroller.ScrollToEnd(); await Paint(guide); Assert(guide.Scroller.VerticalOffset > 0, "안내 끝까지 스크롤 불가"); } finally { guide.Close(); }
            });
            check("D11 상위 폴더에서 일반 이미지·행사 원본 검색", () =>
            {
                var parent = Path.Combine(root, "discover"); var a = Fixture(parent, "일반 A"); var b = Fixture(parent, "일반 B");
                var eventRoot = Path.Combine(parent, "행사 C"); var original = Fixture(eventRoot, Path.Combine("신청서", "[원본]"));
                Fixture(original, "카드"); Fixture(parent, Path.Combine(".숨김", "무관"));
                var scan = FolderDiscovery.Scan(parent);
                Assert(scan.Warnings.Count == 0 && scan.Choices.Count == 3 && scan.Choices.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(new[] {a,b,original}), "대상 폴더 검색/중복 범위 오류");
                Assert(scan.Choices.All(c => !c.IsSelected), "확인 전 전체 선택됨");
            });
            check("D12 폴더 검색 취소·깊이 한도는 명확히 표시", () =>
            {
                var parent = Path.Combine(root, "scan-limit"); var deep = Fixture(parent, Path.Combine("a","b","c","d"));
                var scan = FolderDiscovery.Scan(parent); Assert(scan.Warnings.Count > 0 && scan.Choices.Count == 0, "검색 한도 누락을 빈 결과로 숨김");
                using var token = new CancellationTokenSource(); token.Cancel(); bool cancelled = false;
                try { FolderDiscovery.Scan(parent, token.Token); } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && File.Exists(Path.Combine(deep, "1가상자료.png")), "검색 취소 또는 원본 보존 실패");
            });
            await checkAsync("D13 폴더 선택 창 검색·체크·선택 수 보존", async () =>
            {
                var parent = Path.Combine(root, "picker"); Fixture(parent, "서울"); Fixture(parent, "인천"); Fixture(parent, "부산");
                var picker = new FolderPickerWindow(parent, FolderDiscovery.Scan(parent)) { Left = -16000, Top = -16000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                picker.Show();
                try
                {
                    Assert(!picker.OpenSelectedButton.IsEnabled, "선택 없이 열기 허용");
                    picker.Search.Text = "서울"; picker.SelectVisible(); Assert(picker.SelectedPaths.Count == 1, "검색 밖 폴더도 선택됨");
                    picker.Search.Text = "인천"; picker.SelectVisible(); picker.Search.Clear(); await Paint(picker);
                    Assert(picker.SelectedPaths.Count == 2 && picker.ChoicesList.Items.Count == 3 && picker.OpenSelectedButton.IsEnabled, "검색 전환 시 선택 유실");
                    Capture(picker, Path.Combine(run, "folder-picker.png"));
                }
                finally { picker.Close(); }
            });
            await checkAsync("D14 고른 폴더만 탭으로 열기·중복 방지", () => Window(root, "open-subset", async (w, folder) =>
            {
                var parent = Path.Combine(root, "open-choices"); var a = Fixture(parent, "A"); var b = Fixture(parent, "B"); var c = Fixture(parent, "C");
                await w.OpenChosenFoldersAsync(new[] {a,c,a});
                Assert(w.Tabs.Count == 3 && w.Tabs.All(t => !SafePaths.Same(t.Context.Root, b)) && C<ComboBox>(w, "FolderSelector").Items.Count == 3, "미선택/중복 폴더가 열림");
            }));
            await checkAsync("D15 신청서 목록 폴더 전환·저장·선택 위치 복원", () => Window(root, "switch", async (w, folder) =>
            {
                var a = w.Tabs.Single(); var bPath = Fixture(root, "switch-b"); await w.OpenFolderAsync(bPath); await w.LastPreviewTask; var b = w.Tabs.Last();
                var list = C<ListBox>(w, "ImageList"); list.SelectedIndex = 2; await w.LastNavigationTask; await w.LastPreviewTask; var selected = ((ImageItem)list.SelectedItem).FullPath;
                Click(C<CheckBox>(w, "CardCheck"), true); await w.LastAutoSaveTask;
                C<ComboBox>(w, "FolderSelector").SelectedItem = a; await w.LastNavigationTask; await w.LastPreviewTask;
                Assert(ReferenceEquals(C<ListBox>(w, "WorkTabs").SelectedItem, a), "드롭다운과 탭 불일치");
                C<ComboBox>(w, "FolderSelector").SelectedItem = b; await w.LastNavigationTask; await w.LastPreviewTask;
                Assert(((ImageItem)list.SelectedItem).FullPath == selected && C<CheckBox>(w, "CardCheck").IsChecked == true, "폴더 선택 위치/체크 유실");
            }));
            await checkAsync("D16 폴더 전환 저장 실패 시 이전 폴더·초안 보존", () => Window(root, "switch-error", async (w, folder) =>
            {
                var a = w.Tabs.Single(); var bPath = Fixture(root, "switch-error-b"); await w.OpenFolderAsync(bPath); await w.LastPreviewTask; var b = w.Tabs.Last();
                C<TextBox>(w, "CombinedTextBox").Text = "금지*문자";
                C<ComboBox>(w, "FolderSelector").SelectedItem = a; await w.LastNavigationTask;
                Assert(ReferenceEquals(C<ComboBox>(w, "FolderSelector").SelectedItem, b) && ReferenceEquals(C<ListBox>(w, "WorkTabs").SelectedItem, b) && C<TextBox>(w, "CombinedTextBox").Text == "금지*문자", "실패했는데 폴더/입력이 바뀜");
            }));
            await checkAsync("D17 탭 닫기와 폴더 목록 동기화", () => Window(root, "close-tab", async (w, folder) =>
            {
                var first = w.Tabs.Single(); await w.OpenFolderAsync(Fixture(root, "close-b")); await w.LastPreviewTask;
                await w.CloseTabAsync(first); Assert(C<ComboBox>(w, "FolderSelector").Items.Count == 1, "닫은 탭 잔존");
                await w.CloseTabAsync(w.Tabs.Single()); Assert(C<ComboBox>(w, "FolderSelector").Items.Count == 0 && !C<ComboBox>(w, "FolderSelector").IsEnabled, "마지막 탭 닫기 실패");
            }));
            await checkAsync("D18 4색 테마와 배율을 함께 바꿔도 체크·보기 유지", () => Window(root, "theme-scale", async (w, folder) =>
            {
                Click(C<CheckBox>(w, "AdditionalCheck"), true); await w.LastAutoSaveTask;
                var file = w.AllItems[0].FullPath; var state = LiteWorkspace.StatePath(file); var hash = SafePaths.Hash(state);
                foreach (var p in ThemeManager.Palettes)
                {
                    w.ChangeTheme(p.Id); w.ChangeInterfaceScale(85); await Paint(w);
                    Assert(C<CheckBox>(w, "AdditionalCheck").IsChecked == true && SafePaths.Hash(state) == hash && C<Image>(w, "PreviewImage").Source != null, "테마/배율이 체크를 변경");
                }
            }));
        }
        finally { SettingsStore.DirectoryPath = profile; InterfaceScale.EnsureLoaded(true); ThemeManager.EnsureLoaded(true); }
    }
}
