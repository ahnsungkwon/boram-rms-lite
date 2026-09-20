using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace BoramRms.Lite;

public static class ThemeTests
{
    private static T C<T>(MainWindow w, string name) where T : class => (T)w.FindName(name);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool ColorIs(Brush brush, string hex) => brush is SolidColorBrush solid && solid.Color == (Color)ColorConverter.ConvertFromString(hex);
    private static void Click(CheckBox box, bool value) { box.IsChecked = value; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    private static double Luminance(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        double Linear(byte n) { var x = n / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    }
    private static double Contrast(string a, string b) { var x = Luminance(a); var y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05); }
    private static async Task InWindow(string root, Func<MainWindow, FolderContext, Task> test)
    {
        Directory.CreateDirectory(root); for (int n = 1; n <= 3; n++) SelfTest.AddImage(root, n + "가상테마.png");
        var c = FolderContext.Resolve(root);
        var w = new MainWindow(true) { Left = -16000, Top = -16000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Show();
        try { await w.OpenFolderAsync(root); await w.LastPreviewTask; w.UpdateLayout(); await test(w, c).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { w.ModifiersForTests = null; w.AutoSaveWriterForTests = null; await w.FlushStatusSavesAsync(); await w.ReloadAsync(); await w.LastPreviewTask; w.Close(); }
    }
    private static async Task Key(MainWindow w, ModifierKeys mods)
    {
        w.ModifiersForTests = () => mods;
        try
        {
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), Environment.TickCount, System.Windows.Input.Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            C<TextBox>(w, "CombinedTextBox").RaiseEvent(e); Assert(e.Handled, "Space 단축키 미처리"); await w.LastShortcutTask; await w.LastPreviewTask;
        }
        finally { w.ModifiersForTests = null; }
    }
    private static async Task Paint(MainWindow w) { w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static void Capture(MainWindow w, string path, int width, int height)
    {
        w.Width = width; w.Height = height; w.UpdateLayout();
        var element = (FrameworkElement)w.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); png.Save(output);
    }
    public static async Task RunAsync(string run, Action<string, Action> check, Func<string, Func<Task>, Task> checkAsync)
    {
        var previous = SettingsStore.DirectoryPath;
        var root = Path.Combine(run, "themes"); Directory.CreateDirectory(root);
        SettingsStore.DirectoryPath = Path.Combine(root, "profile"); ThemeManager.EnsureLoaded(true);
        try
        {
            check("T01 4개 팔레트·리소스 일치와 주요 글자 대비", () =>
            {
                Assert(ThemeManager.Palettes.Select(p => p.Id).SequenceEqual(new[] { "green", "blue", "purple", "pink" }), "테마 목록 불일치");
                foreach (var p in ThemeManager.Palettes)
                {
                    Assert(p.Colors.Keys.Order().SequenceEqual(ThemeManager.Palettes[0].Colors.Keys.Order()), "팔레트 키 누락");
                    Assert(Contrast(p.Accent, "#FFFFFF") >= 4.5 && Contrast(p.Text, "#FFFFFF") >= 7 && Contrast(p.Muted, p.Background) >= 4.5, "글자 대비 부족: " + p.Id);
                }
            });
            await checkAsync("T02 실제 메뉴로 4개 테마 전환·현재 화면 즉시 적용", () => InWindow(Path.Combine(root, "palette"), async (w, c) =>
            {
                Click(C<CheckBox>(w, "CardCheck"), true); await w.LastAutoSaveTask;
                var reason = C<UniformGrid>(w, "RepairReasons").Children.OfType<CheckBox>().First(); Click(reason, true); await w.LastAutoSaveTask;
                var menu = C<Button>(w, "ThemeButton").ContextMenu;
                foreach (var p in ThemeManager.Palettes)
                {
                    menu.Items.OfType<MenuItem>().Single(i => (string)i.Tag == p.Id).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Paint(w);
                    Assert(ThemeManager.Current.Id == p.Id && ColorIs(w.Background, p.Background) && ColorIs(C<Border>(w, "AppHeader").Background, p.Header), "메인 배경/헤더 색상 미변경: " + p.Id);
                    Assert(ColorIs(C<Button>(w, "SaveNextButton").Background, p.Accent) && ColorIs(C<TextBox>(w, "CombinedTextBox").BorderBrush, p.Focus), "버튼/입력칸 색상 미변경");
                    Assert(ColorIs(C<Border>(w, "ImageStage").Background, p.Canvas) && ColorIs(C<Button>(w, "NextImageRail").Foreground, p.Accent), "중앙/탐색 색상 미변경");
                    var box = C<CheckBox>(w, "CardCheck"); box.ApplyTemplate();
                    Assert(ColorIs(((Border)box.Template.FindName("CheckSurface", box)).Background, p.Accent), "체크 색상 미변경");
                    var list = C<ListBox>(w, "ImageList"); var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem); row.ApplyTemplate();
                    Assert(ColorIs(((Border)row.Template.FindName("Row", row)).Background, p.Selection), "선택 행 색상 미변경");
                    var bar = ((StackPanel)C<UniformGrid>(w, "QuotaProgress").Children[0]).Children.OfType<ProgressBar>().Single();
                    Assert(ColorIs(bar.Foreground, p.Accent), "진행 표시 색상 미변경");
                    Capture(w, Path.Combine(run, "theme-" + p.Id + ".png"), 1424, 941);
                }
            }));
            await checkAsync("T03 테마 저장·다시 읽기·새 창과 작업 설정 분리", () => InWindow(Path.Combine(root, "persist"), async (w, c) =>
            {
                SettingsStore.Save(new SavedSettings { Folders = new() { c.Root }, Selected = new() { [c.Root] = w.AllItems[0].FullPath } });
                var settings = Path.Combine(SettingsStore.DirectoryPath, "settings.json"); var before = SafePaths.Hash(settings);
                Assert(w.ChangeTheme("purple"), "설정 저장 실패"); ThemeManager.Select("blue", false); ThemeManager.EnsureLoaded(true);
                Assert(ThemeManager.Current.Id == "purple" && SafePaths.Hash(settings) == before, "저장한 테마 복원 실패 또는 작업 설정 변경");
                var next = new MainWindow(true); try { Assert(ColorIs(next.Background, ThemeManager.Current.Background) && C<TextBlock>(next, "ThemeLabel").Text.Contains("보라"), "새 창 테마 미반영"); } finally { next.Close(); }
                await Paint(w);
            }));
            await checkAsync("T04 테마 변경 중 미완성 이름·커서·선택·확대 유지", () => InWindow(Path.Combine(root, "draft"), async (w, c) =>
            {
                var input = C<TextBox>(w, "CombinedTextBox"); input.Text = "아직작성중"; input.Select(2, 2);
                w.SetViewport(2.77, 30, -80); var image = C<Image>(w, "PreviewImage").Source; var selected = C<ListBox>(w, "ImageList").SelectedItem;
                var hash = SafePaths.Hash(w.AllItems[0].FullPath);
                foreach (var p in ThemeManager.Palettes)
                {
                    Assert(w.ChangeTheme(p.Id), "테마 변경 실패"); await Paint(w);
                    Assert(input.Text == "아직작성중" && input.SelectionStart == 2 && input.SelectionLength == 2 && w.HasPendingRename, "작성 중 이름/커서 초기화");
                    Assert(ReferenceEquals(image, C<Image>(w, "PreviewImage").Source) && ReferenceEquals(selected, C<ListBox>(w, "ImageList").SelectedItem), "이미지 다시 읽기 또는 선택 이동");
                    Assert(C<ScaleTransform>(w, "ImageScale").ScaleX == 2.77 && C<TranslateTransform>(w, "ImageTranslate").Y == -80, "테마 전환으로 이미지 맞춤 발생");
                }
                Assert(SafePaths.Hash(w.AllItems[0].FullPath) == hash && !Directory.Exists(Path.Combine(c.Root, ".rmslite")), "테마 전환이 업무 저장 실행");
            }));
            await checkAsync("T05 필터·정렬·다중 선택 유지", () => InWindow(Path.Combine(root, "selection"), async (w, c) =>
            {
                C<TextBox>(w, "SearchText").Text = "가상"; await w.LastNavigationTask; await w.LastPreviewTask;
                C<ComboBox>(w, "SortMode").SelectedIndex = 1; await w.LastNavigationTask; await w.LastPreviewTask;
                var list = C<ListBox>(w, "ImageList"); list.SelectedItems.Add(list.Items[1]); var selected = list.SelectedItems.Cast<object>().ToArray(); var order = list.Items.Cast<object>().ToArray();
                w.ChangeTheme("pink"); await Paint(w);
                Assert(selected.SequenceEqual(list.SelectedItems.Cast<object>()) && order.SequenceEqual(list.Items.Cast<object>()) && C<TextBox>(w, "SearchText").Text == "가상" && C<ComboBox>(w, "SortMode").SelectedIndex == 1, "목록 상태 초기화");
            }));
            await checkAsync("T06 체크 저장 진행 중 테마 변경은 저장 내용 보존", () => InWindow(Path.Combine(root, "in-flight"), async (w, c) =>
            {
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                w.AutoSaveWriterForTests = async (ctx, item, state) => { await gate.Task; return LiteWorkspace.Save(ctx, item, null, state); };
                try { Click(C<CheckBox>(w, "CardCheck"), true); Assert(w.ChangeTheme("blue"), "저장 중 테마 변경 차단"); }
                finally { gate.TrySetResult(true); }
                await w.LastAutoSaveTask;
                Assert(LiteWorkspace.Load(c).First().Card && !w.HasPendingStatus && ThemeManager.Current.Id == "blue", "체크 저장 누락");
            }));
            await checkAsync("T07 모든 테마에서 빈 이름 Space·Ctrl+Space 이동", () => InWindow(Path.Combine(root, "keys"), async (w, c) =>
            {
                foreach (var p in ThemeManager.Palettes)
                {
                    w.ChangeTheme(p.Id); C<TextBox>(w, "CombinedTextBox").Clear(); await Key(w, ModifierKeys.None);
                    Assert(C<ListBox>(w, "ImageList").SelectedIndex == 1, "빈 이름 다음 실패: " + p.Id);
                    C<TextBox>(w, "CombinedTextBox").Clear(); await Key(w, ModifierKeys.Control);
                    Assert(C<ListBox>(w, "ImageList").SelectedIndex == 0 && !w.HasPendingRename, "Ctrl+Space 이전 실패: " + p.Id);
                }
            }));
            await checkAsync("T08 열려 있는 분리창·업데이트창 즉시 동기화", () => InWindow(Path.Combine(root, "windows"), async (w, c) =>
            {
                var panel = new PanelWindow("테마 검수", new Button { Content = "테스트" }, 360) { Left = -16000, Top = -16000, ShowInTaskbar = false, ShowActivated = false };
                var update = new UpdateWindow(true) { Left = -16000, Top = -16000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                panel.Show(); update.Show();
                try
                {
                    foreach (var p in ThemeManager.Palettes)
                    {
                        w.ChangeTheme(p.Id); await Paint(w);
                        Assert(ColorIs(panel.Foreground, p.Text) && ColorIs(((DockPanel)((DockPanel)panel.Content).Children[0]).Background, p.Header), "분리창 색상 미동기화");
                        Assert(ColorIs(update.Background, p.Background) && ColorIs(((Grid)update.Content).Background, p.Background), "업데이트창 색상 미동기화");
                        Assert(ReferenceEquals(w.Icon, panel.Icon) && ReferenceEquals(w.Icon, update.Icon), "보조 창 아이콘 미동기화");
                    }
                }
                finally { update.Close(); panel.Close(); }
            }));
            await checkAsync("T09 사용 안내 창의 테마와 테마 설명", () => InWindow(Path.Combine(root, "help"), async (w, c) =>
            {
                C<Button>(w, "WorkflowHelpButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var help = w.OwnedWindows.Cast<Window>().Single(i => i.Title.Contains("사용 안내"));
                try { w.ChangeTheme("purple"); await Paint(w); Assert(ColorIs(help.Foreground, ThemeManager.Current.Text) && ReferenceEquals(help.Icon, w.Icon) && MainWindow.WorkflowHelpText.Contains("녹색·블루·보라·핑크"), "도움말 테마/설명 누락"); }
                finally { help.Close(); }
            }));
            check("T10 오류·보완·카드·구좌 의미색은 모든 테마에서 유지", () =>
            {
                var item = new ImageItem { Status = "보완", Card = true, Quota = "4" }; var colors = new[] { item.StatusBackground.ToString(), item.StatusForeground.ToString(), item.QuotaBrush.ToString() };
                foreach (var p in ThemeManager.Palettes)
                {
                    ThemeManager.Select(p.Id, false);
                    Assert(colors.SequenceEqual(new[] { item.StatusBackground.ToString(), item.StatusForeground.ToString(), item.QuotaBrush.ToString() }) && ColorIs((Brush)Application.Current.FindResource("ErrorBrush"), "#AC4942"), "의미색 변경");
                }
            });
            await checkAsync("T11 신청서 픽셀·파일 내용·수정시각과 체크 기록 무변경", () => InWindow(Path.Combine(root, "pixels"), async (w, c) =>
            {
                Click(C<CheckBox>(w, "AdditionalCheck"), true); await w.LastAutoSaveTask;
                var files = Directory.GetFiles(c.Root, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => (SafePaths.Hash(p), File.GetLastWriteTimeUtc(p)));
                var source = (BitmapSource)C<Image>(w, "PreviewImage").Source; var stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
                var before = new byte[source.PixelHeight * stride]; source.CopyPixels(before, stride, 0);
                foreach (var p in ThemeManager.Palettes) { w.ChangeTheme(p.Id); await Paint(w); }
                var after = new byte[before.Length]; ((BitmapSource)C<Image>(w, "PreviewImage").Source).CopyPixels(after, stride, 0);
                Assert(before.SequenceEqual(after) && files.All(p => SafePaths.Hash(p.Key) == p.Value.Item1 && File.GetLastWriteTimeUtc(p.Key) == p.Value.Item2), "테마가 신청서/상태를 변경");
            }));
            check("T12 없거나 손상된 테마 설정은 녹색 기본값", () =>
            {
                File.WriteAllText(ThemeManager.PreferencePath, "{bad json"); ThemeManager.EnsureLoaded(true); Assert(ThemeManager.Current.Id == "green", "잘못된 JSON 복구 실패");
                File.WriteAllText(ThemeManager.PreferencePath, "{\"Theme\":\"unrecognized\"}"); ThemeManager.EnsureLoaded(true); Assert(ThemeManager.Current.Id == "green", "알 수 없는 테마 복구 실패");
                ThemeManager.Select("green");
            });
            await checkAsync("T13 테마 설정 쓰기 실패는 이전 색상과 입력 보존", () => InWindow(Path.Combine(root, "locked-settings"), async (w, c) =>
            {
                w.ChangeTheme("blue"); C<TextBox>(w, "CombinedTextBox").Text = "작성중";
                var before = File.ReadAllBytes(ThemeManager.PreferencePath);
                using (var locked = new FileStream(ThemeManager.PreferencePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert(!w.ChangeTheme("pink") && ThemeManager.Current.Id == "blue" && C<TextBox>(w, "CombinedTextBox").Text == "작성중", "실패를 성공으로 표시/입력 변경");
                Assert(File.ReadAllBytes(ThemeManager.PreferencePath).SequenceEqual(before), "기존 테마 설정 손상"); await Paint(w);
            }));
            await checkAsync("T14 메뉴 선택 표시·색상명·단축키 충돌 방지", () => InWindow(Path.Combine(root, "menu"), async (w, c) =>
            {
                var button = C<Button>(w, "ThemeButton"); var menu = button.ContextMenu;
                Assert(menu.Items.Count == 4 && !w.CanHandleSpace(button), "메뉴 개수/키보드 충돌");
                foreach (var p in ThemeManager.Palettes)
                {
                    var item = menu.Items.OfType<MenuItem>().Single(i => (string)i.Tag == p.Id); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert(item.IsChecked && menu.Items.OfType<MenuItem>().Count(i => i.IsChecked) == 1 && C<TextBlock>(w, "ThemeLabel").Text.Contains(p.Label) && !w.CanHandleSpace(item), "선택 테마 표시 불일치");
                }
                await Paint(w);
            }));
            await checkAsync("T15 작은 창에서 테마 메뉴와 세 패널 접근 유지", () => InWindow(Path.Combine(root, "small"), async (w, c) =>
            {
                w.Width = 1060; w.Height = 680; w.ChangeTheme("blue"); await Paint(w);
                var b = C<Button>(w, "ThemeButton"); var pos = b.TranslatePoint(new Point(), w);
                Assert(b.IsVisible && pos.X >= 0 && pos.X + b.ActualWidth <= w.ActualWidth && C<Border>(w, "ImageStage").ActualWidth > 250, "좁은 창 테마/이미지 잘림");
                Capture(w, Path.Combine(run, "theme-small.png"), 1060, 680);
            }));
            await checkAsync("T16 실제 이미지 패널 분리·색상 변경·복귀", () => InWindow(Path.Combine(root, "dock"), async (w, c) =>
            {
                var host = C<ContentControl>(w, "ImageHost"); var content = host.Content;
                w.ToggleDock("image", host, "신청서 이미지", 760);
                try
                {
                    w.ChangeTheme("pink"); await Paint(w);
                    var panel = w.OwnedWindows.Cast<Window>().Single(i => i is PanelWindow);
                    Assert(ColorIs(panel.Foreground, ThemeManager.Current.Text) && ColorIs(C<Border>(w, "ImageStage").Background, ThemeManager.Current.Canvas) && C<Image>(w, "PreviewImage").Source != null, "실제 분리 패널 미적용");
                }
                finally { w.ToggleDock("image", host, "신청서 이미지", 760); }
                Assert(ReferenceEquals(content, host.Content), "분리 패널 복귀 실패");
            }));
        }
        finally { SettingsStore.DirectoryPath = previous; ThemeManager.EnsureLoaded(true); }
    }
}
