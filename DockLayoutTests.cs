using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace BoramRms.Lite;

public static class DockLayoutTests
{
    private static T C<T>(MainWindow w, string name) where T : class => (T)w.FindName(name);
    private static void Assert(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Paint(MainWindow w) { w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); w.UpdateLayout(); }
    private static void Toggle(MainWindow w, string key) => w.ToggleDock(key, C<ContentControl>(w, key == "rename" ? "LeftHost" : key == "list" ? "RightHost" : "ImageHost"), key == "rename" ? "리네임" : key == "list" ? "신청서 목록" : "신청서 이미지", key == "image" ? 760 : 360);
    private static void Capture(MainWindow w, string path)
    {
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(w); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var file = File.Create(path); png.Save(file);
    }
    private static async Task InWindow(string root, string name, Func<MainWindow, string, Task> action, int imageWidth = 800, int imageHeight = 1120)
    {
        var folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        for (var n = 1; n <= 3; n++) SelfTest.AddImage(folder, n + "가상자료.png", imageWidth, imageHeight);
        var w = new MainWindow(true) { Left = -16000, Top = -16000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Width = 1100, Height = 900 };
        w.Show();
        try { w.ChangeInterfaceScale(100); await w.OpenFolderAsync(folder); await w.LastPreviewTask; await Paint(w); await action(w, folder); }
        finally { w.ModifiersForTests = null; await w.FlushStatusSavesAsync(); await w.ReloadAsync(); await w.LastPreviewTask; w.Close(); }
    }
    public static async Task RunAsync(string run, Func<string, Func<Task>, Task> check)
    {
        var root = Path.Combine(run, "dock-layout"); Directory.CreateDirectory(root);
        var profile = SettingsStore.DirectoryPath; SettingsStore.DirectoryPath = Path.Combine(root, "profile");
        try
        {
            await check("K01 리네임 분리: 빈 열·분할선 제거 후 이미지 영역 확대", () => InWindow(root, "left", async (w, folder) =>
            {
                var before = C<Border>(w, "ImageStage").ActualWidth; var left = C<ColumnDefinition>(w, "LeftColumn").ActualWidth;
                Toggle(w, "rename"); await Paint(w);
                Assert(C<ColumnDefinition>(w, "LeftColumn").ActualWidth < .5 && C<GridSplitter>(w, "LeftSplitter").Visibility == Visibility.Collapsed, "리네임 빈 열이 남음");
                Assert(C<Border>(w, "ImageStage").ActualWidth >= before + left + 6, "리네임 공간이 이미지로 이동하지 않음");
                Assert(C<Button>(w, "RestoreRenamePanelButton").IsVisible, "복귀 버튼 누락");
            }));
            await check("K02 양쪽 분리: 세로·긴 세로·가로 최대 맞춤 및 실제 확대", async () =>
            {
                // Fit must be derived from BOTH dimensions. A fixed +50 threshold is
                // invalid when the monitor height caps the paper before that increase.
                foreach (var sample in new[] { (Name: "portrait", W: 800, H: 1120), (Name: "tall", W: 600, H: 1800), (Name: "landscape", W: 1120, H: 800) })
                    await InWindow(root, "both-" + sample.Name, async (w, folder) =>
                    {
                        var image = C<Image>(w, "PreviewImage"); var stage = C<Border>(w, "ImageStage");
                        var before = image.Width; var beforeStageWidth = stage.ActualWidth; var beforeStageHeight = stage.ActualHeight;
                        var ratio = sample.W / (double)sample.H;
                        var expectedBefore = Math.Min(beforeStageWidth - 24, (beforeStageHeight - 24) * ratio);
                        var suffix = sample.Name == "portrait" ? "" : "-" + sample.Name;
                        Capture(w, Path.Combine(run, "dock-before" + suffix + ".png"));
                        Toggle(w, "rename"); Toggle(w, "list"); await Paint(w);
                        var area = C<Grid>(w, "WorkArea"); var host = C<ContentControl>(w, "ImageHost");
                        Capture(w, Path.Combine(run, "dock-expanded" + suffix + ".png"));
                        var expected = Math.Min(stage.ActualWidth - 24, (stage.ActualHeight - 24) * ratio);
                        File.WriteAllText(Path.Combine(run, "DOCK_MEASUREMENTS" + suffix + ".json"), System.Text.Json.JsonSerializer.Serialize(new { sample = sample.Name, beforeImageWidth = before, beforeStageWidth, beforeStageHeight, afterImageWidth = image.Width, afterStageWidth = stage.ActualWidth, afterStageHeight = stage.ActualHeight, expandedPanelWidth = host.ActualWidth, availableWidth = area.ActualWidth, expectedBefore, expectedImageWidth = expected }));
                        Assert(Math.Abs(host.ActualWidth - area.ActualWidth) < 2 && area.ColumnDefinitions.Where((_, i) => i != 2).All(c => c.ActualWidth < .5), "양쪽 빈 공간 잔존");
                        Assert(Math.Abs(before - expectedBefore) < 2 && Math.Abs(image.Width - expected) < 2, "분리 전후 최대 맞춤 크기 불일치: " + sample.Name);
                        Assert(Math.Abs(image.Width / image.Height - ratio) < .001, "원본 종횡비 왜곡");
                        Assert(image.Width <= stage.ActualWidth - 23 && image.Height <= stage.ActualHeight - 23, "전체 맞춤 이미지 잘림");
                        Assert(C<ScaleTransform>(w, "ImageScale").ScaleX == 1 && C<TranslateTransform>(w, "ImageTranslate").X == 0 && C<TranslateTransform>(w, "ImageTranslate").Y == 0, "분리 후 확대·위치 초기 맞춤 누락");
                        if (expected > expectedBefore + 2) Assert(image.Width > before + 2, "활용 가능한 공간이 늘었는데 실제 종이가 확대되지 않음");
                        if (sample.Name == "landscape") Assert(expected > expectedBefore + 50 && image.Width > before + 50, "가로 이미지의 충분한 확대 검증 누락");
                        if (sample.Name == "tall") Assert(Math.Abs(image.Width - (stage.ActualHeight - 24) * ratio) < 2, "높이 제한 세로 이미지 맞춤 오류");
                    }, sample.W, sample.H);
            });
            await check("K03 목록만 분리·닫기 복귀: 사용자 패널 너비 복원", () => InWindow(root, "right", async (w, folder) =>
            {
                C<ColumnDefinition>(w, "RightColumn").Width = new GridLength(330); await Paint(w);
                var before = C<Border>(w, "ImageStage").ActualWidth;
                Toggle(w, "list"); await Paint(w);
                Assert(C<Border>(w, "ImageStage").ActualWidth >= before + 336 && C<ContentControl>(w,"RightHost").Visibility == Visibility.Collapsed, "목록 공간 미반환");
                w.OwnedWindows.OfType<PanelWindow>().Single().Close(); await Paint(w);
                Assert(Math.Abs(C<ColumnDefinition>(w, "RightColumn").ActualWidth - 330) < 2 && C<GridSplitter>(w,"RightSplitter").IsVisible && C<Button>(w,"RestoreListPanelButton").Visibility == Visibility.Collapsed, "목록 복귀 너비/분할선 오류");
            }));
            await check("K04 복귀 버튼과 반복 분리: 폭·원래 컨트롤 보존", () => InWindow(root, "repeat", async (w, folder) =>
            {
                var left = C<ContentControl>(w,"LeftHost").Content; var right = C<ContentControl>(w,"RightHost").Content;
                var lw = w.DockedPanelWidth("rename"); var rw = w.DockedPanelWidth("list");
                for (var n=0;n<4;n++)
                {
                    Toggle(w,"rename"); Toggle(w,"list"); await Paint(w);
                    C<Button>(w,"RestoreListPanelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    C<Button>(w,"RestoreRenamePanelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Paint(w);
                    Assert(ReferenceEquals(left,C<ContentControl>(w,"LeftHost").Content) && ReferenceEquals(right,C<ContentControl>(w,"RightHost").Content) && Math.Abs(w.DockedPanelWidth("rename")-lw)<1 && Math.Abs(w.DockedPanelWidth("list")-rw)<1, "반복 분리로 너비/컨트롤 유실");
                }
            }));
            await check("K05 분리·테마·배율 전환: 이름 초안·커서·체크·원본 보존", () => InWindow(root, "draft", async (w, folder) =>
            {
                var card = C<CheckBox>(w,"CardCheck"); card.IsChecked=true; card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await w.LastAutoSaveTask;
                var input = C<TextBox>(w,"CombinedTextBox"); input.Text="작성중"; input.Select(1,1);
                var files = Directory.GetFiles(folder,"*",SearchOption.AllDirectories).ToDictionary(p=>p,SafePaths.Hash);
                var selected = C<ListBox>(w,"ImageList").SelectedItem;
                Toggle(w,"rename"); Toggle(w,"list"); await Paint(w);
                foreach(var scale in new[]{80,90,100}) { w.ChangeInterfaceScale(scale); w.ChangeTheme("purple"); await Paint(w); Assert(C<ContentControl>(w,"ImageHost").ActualWidth >= C<Grid>(w,"WorkArea").ActualWidth-2,"배율 변경 시 빈 열 복원됨"); }
                Assert(input.Text=="작성중" && input.SelectionStart==1 && input.SelectionLength==1 && card.IsChecked==true && ReferenceEquals(selected,C<ListBox>(w,"ImageList").SelectedItem) && files.All(p=>SafePaths.Hash(p.Key)==p.Value),"분리 동작이 입력/체크/원본 변경");
            }));
            await check("K06 분리창 Space·Ctrl+Space와 빈 이름 이동", () => InWindow(root, "keys", async (w, folder) =>
            {
                Toggle(w,"rename"); Toggle(w,"list"); await Paint(w); var input=C<TextBox>(w,"CombinedTextBox"); input.Clear();
                foreach(var modifiers in new[]{ModifierKeys.None,ModifierKeys.Control})
                {
                    w.ModifiersForTests=()=>modifiers;
                    var args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(input),Environment.TickCount,Key.Space){RoutedEvent=Keyboard.PreviewKeyDownEvent}; input.RaiseEvent(args);
                    Assert(args.Handled,"분리창 키 이벤트 누락"); await w.LastShortcutTask; await w.LastPreviewTask;
                    Assert(C<ListBox>(w,"ImageList").SelectedIndex==(modifiers==ModifierKeys.None?1:0),"분리창 양방향 이동 실패");
                }
            }));
            await check("K07 이미지까지 분리 후 복귀해도 전체 너비 유지", () => InWindow(root, "image", async (w, folder) =>
            {
                Toggle(w,"rename"); Toggle(w,"list"); Toggle(w,"image"); await Paint(w); Toggle(w,"image"); await Paint(w);
                Assert(C<ContentControl>(w,"ImageHost").ActualWidth >= C<Grid>(w,"WorkArea").ActualWidth-2 && C<Button>(w,"RestoreRenamePanelButton").IsVisible && C<Image>(w,"PreviewImage").Source!=null,"이미지 분리/복귀에서 사이드 공간 재생성");
            }));
            await check("K08 분리 상태 종료·재시작용 너비에 0 저장 방지", () => InWindow(root, "settings", async (w, folder) =>
            {
                C<ColumnDefinition>(w,"LeftColumn").Width=new GridLength(360); C<ColumnDefinition>(w,"RightColumn").Width=new GridLength(300); await Paint(w);
                Toggle(w,"rename"); Toggle(w,"list"); await Paint(w);
                Assert(w.DockedPanelWidth("rename")==360 && w.DockedPanelWidth("list")==300,"분리 상태 저장 너비가 0");
                w.Close(); var saved=SettingsStore.Load(); Assert(saved.LeftWidth==360 && saved.RightWidth==300,"종료 후 원래 너비 유실");
            }));
        }
        finally { SettingsStore.DirectoryPath=profile; InterfaceScale.EnsureLoaded(true); ThemeManager.EnsureLoaded(true); }
    }
}
