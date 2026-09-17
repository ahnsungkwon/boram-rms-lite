using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace BoramRms.Lite;

public static class BrandInputTests
{
    private static void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static MainWindow Window() => new(testMode: true) { Left = -16000, Top = -16000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
    private static void CheckTextBox(TextBox input)
    {
        input.ApplyTemplate(); input.UpdateLayout();
        var host = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
        Assert(host.Margin == new Thickness(0), "입력 패딩이 호스트 여백으로 중복 적용됨");
        Assert(input.ActualHeight >= 62 && input.VerticalContentAlignment == VerticalAlignment.Center, "입력칸 높이/수직 정렬");
        Assert(host.ScrollableHeight < .5, "한 줄 글자에 세로 스크롤/잘림 발생");
        for (int i = 0; i < input.Text.Length; i++)
        {
            var rect = input.GetRectFromCharacterIndex(i);
            Assert(!rect.IsEmpty && rect.Top >= 1 && rect.Bottom <= input.ActualHeight - 1, "글자 상하 경계가 입력칸 밖: " + rect);
        }
    }
    private static void RenderElement(FrameworkElement element, string path, double scale)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
    public static async Task RunAsync(string run, Action<string, Action> check, Func<string, Func<Task>, Task> checkAsync)
    {
        var root = Path.Combine(run, "brand-input"); Directory.CreateDirectory(root);
        check("59 구좌·이름 입력칸 글자 상하 경계 및 여백 검증", () =>
        {
            var w = Window(); w.Show();
            try
            {
                var input = (TextBox)w.FindName("CombinedTextBox"); input.Text = "2가상명재"; w.UpdateLayout(); CheckTextBox(input);
                input.SelectAll(); RenderElement(input, Path.Combine(run, "rename-input-preview.png"), 2);
            }
            finally { ((TextBox)w.FindName("CombinedTextBox")).Text = ""; w.ReloadAsync().GetAwaiter().GetResult(); w.Close(); }
        });
        check("60 한글/영문 및 100·125·150·200% 배율 렌더링", () =>
        {
            var w = Window(); w.Show();
            try
            {
                var input = (TextBox)w.FindName("CombinedTextBox");
                var fonts = new[] { "Pretendard, Malgun Gothic, Segoe UI", "Malgun Gothic", "Segoe UI" };
                int count = 0;
                foreach (var font in fonts)
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    input.FontFamily = new FontFamily(font); input.LayoutTransform = new ScaleTransform(scale, scale);
                    foreach (var text in new[] { "2가상명재", "99김가상", "1Agjpqy" }) { input.Text = text; w.UpdateLayout(); CheckTextBox(input); }
                    RenderElement(input, Path.Combine(root, "input-" + (++count) + ".png"), scale);
                }
                Assert(count == 12, "폰트·배율 조합 누락");
            }
            finally { w.ReloadAsync().GetAwaiter().GetResult(); w.Close(); }
        });
        await checkAsync("61 전화번호 매칭 UI 제거 및 기존 결과 파일 미사용", async () =>
        {
            var folder = Path.Combine(root, "no-phone-read"); Directory.CreateDirectory(folder);
            SelfTest.AddImage(folder, "2가상명재.png");
            var workbook = Path.Combine(folder, "전화번호_조회결과.xlsx"); File.WriteAllText(workbook, "SYNTHETIC SENTINEL - NOT AN XLSX");
            var before = SafePaths.Hash(workbook);
            using (var locked = new FileStream(workbook, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var w = Window(); w.Show();
                try
                {
                    await w.OpenFolderAsync(folder); await w.LastPreviewTask;
                    Assert(w.AllItems.Count == 1 && ((Image)w.FindName("PreviewImage")).Source != null, "관련 없는 결과 파일 때문에 이미지 열기 실패");
                    Assert(w.FindName("PhoneSummary") == null, "전화번호 문구 잔존");
                    Assert(!((ComboBox)w.FindName("StatusFilter")).Items.Cast<string>().Any(t => t.Contains("전화")), "전화번호 필터 잔존");
                    Assert(!((TextBox)w.FindName("LogBox")).Text.Contains("전화번호"), "불필요한 전화번호 작업 기록");
                    SelfTest.Render(w, Path.Combine(run, "brand-main-preview.png"), 1424,941);
                    var panel = (FrameworkElement)w.FindName("LeftPanel"); RenderElement(panel, Path.Combine(run, "rename-panel-preview.png"), 1.5);
                }
                finally { w.Close(); }
            }
            Assert(SafePaths.Hash(workbook) == before, "기존 결과 파일 변경");
        });
        check("62 9개 해상도 ICO 및 녹색·흰색 픽셀 검증", () =>
        {
            using var stream = Application.GetResourceStream(new Uri(AppBrand.IconUri)).Stream;
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var sizes = decoder.Frames.Select(f => f.PixelWidth).OrderBy(n => n).ToArray();
            Assert(sizes.SequenceEqual(new[] {16,20,24,32,40,48,64,128,256}), "ICO 크기 목록 오류");
            var frame = decoder.Frames.Single(f => f.PixelWidth == 32);
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); var pixels = new byte[32 * 32 * 4]; bitmap.CopyPixels(pixels, 32 * 4, 0);
            var green = 0; var white = 0;
            for (int i = 0; i < pixels.Length; i += 4) { if (pixels[i+3] > 200 && pixels[i+1] > pixels[i+2] + 25) green++; if (pixels[i] > 240 && pixels[i+1] > 240 && pixels[i+2] > 240 && pixels[i+3] > 200) white++; }
            Assert(green > 400 && white > 25, "녹색 배경/흰 RMS 누락");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(decoder.Frames.Single(f => f.PixelWidth == 256)); using var output = File.Create(Path.Combine(run, "rms-icon-preview.png")); encoder.Save(output);
        });
        check("63 메인·분리·업데이트 창 아이콘 연결", () =>
        {
            var main = Window(); var panel = new PanelWindow("아이콘 검수", new Border(), 360); var update = new UpdateWindow(testMode:true);
            try { Assert(main.Icon != null && panel.Icon != null && update.Icon != null, "창 아이콘 연결 누락"); Assert(Application.Current.FindResource("RmsIcon") is DrawingImage, "상단 브랜드 아이콘 누락"); }
            finally { main.Close(); panel.Close(); update.Close(); }
        });
    }
}
