using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
namespace BoramRms.Lite;
public sealed class ResizeWindow : Window
{
    private readonly FolderContext _context;
    private readonly IReadOnlyList<ImageItem>[] _groups;
    private readonly ComboBox _scope = new(), _dimension = new(), _format = new();
    private readonly TextBox _size = new() { Text = "300" }, _pixels = new() { Text = "1040" }, _quality = new() { Text = "95" }, _output = new();
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ProgressBar _bar = new() { Height = 8, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _start = new() { Content = "사본 만들기" }, _cancel = new() { Content = "작업 취소", IsEnabled = false }, _open = new() { Content = "결과 폴더 열기", IsEnabled = false };
    private readonly StackPanel _inputs = new();
    private CancellationTokenSource? _cancellation;
    private string? _lastOutput;
    public bool IsRunning { get; private set; }
    public ResizeManifest? Result { get; private set; }
    public ResizeWindow(FolderContext context, IReadOnlyList<ImageItem> selected, IReadOnlyList<ImageItem> visible, IReadOnlyList<ImageItem> all)
    {
        _context = context; _groups = new[] { selected.ToArray(), visible.ToArray(), all.ToArray() };
        Background = (Brush)FindResource("AppBg"); Foreground = (Brush)FindResource("TextBrush"); FontFamily = new FontFamily("Malgun Gothic"); FontSize = 12;
        Title = "보람 RMS Lite · 이미지 리사이즈"; Width = 680; Height = 760; MinWidth = 600; MinHeight = 660; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(20), Background = (Brush)FindResource("AppBg") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock { Text = "원본은 그대로, 사본은 가볍게", FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) };
        var top = new StackPanel(); top.Children.Add(title); top.Children.Add(_inputs); root.Children.Add(top);
        _scope.ItemsSource = new[] { $"선택한 이미지 · {selected.Count}개", $"현재 표시 목록 · {visible.Count}개", $"현재 탭 전체 · {all.Count}개" };
        _scope.SelectedIndex = selected.Count > 0 ? 0 : 1;
        AddRow("처리 범위", _scope);
        var numbers = new Grid(); numbers.ColumnDefinitions.Add(new ColumnDefinition()); numbers.ColumnDefinitions.Add(new ColumnDefinition()); numbers.ColumnDefinitions.Add(new ColumnDefinition());
        AddNumber(numbers, 0, "최대 용량 (KB)", _size); AddNumber(numbers, 1, "최대 크기 (px)", _pixels); AddNumber(numbers, 2, "JPEG 최대 품질", _quality); _inputs.Children.Add(numbers);
        _dimension.ItemsSource = new[] { "가로 너비 기준 · 기존 기본값", "긴 변 기준" }; _dimension.SelectedIndex = 0;
        _format.ItemsSource = new[] { "JPEG · 투명 배경은 흰색", "PNG · 투명 배경 유지" }; _format.SelectedIndex = 0;
        AddRow("크기 기준", _dimension); AddRow("출력 형식", _format);
        _output.Text = Path.Combine(context.Root, "리사이즈_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var outputRow = new DockPanel(); var browse = new Button { Content = "위치 선택", MinWidth = 80 }; DockPanel.SetDock(browse, Dock.Right); outputRow.Children.Add(browse); outputRow.Children.Add(_output); AddRow("새 결과 폴더", outputRow);
        browse.Click += (_, _) => { var d = new OpenFolderDialog { Title = "결과 폴더를 만들 상위 폴더 선택" }; if (d.ShowDialog(this) == true) _output.Text = Path.Combine(d.FolderName, "리사이즈_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")); };
        var hint = new TextBlock { Text = "새 폴더 또는 빈 폴더에만 저장합니다. 원본 덮어쓰기·확대·잘라내기는 하지 않습니다. 용량에 맞춰 품질을 조절하고 필요할 때 크기를 더 줄입니다. 여러 페이지 TIFF·애니메이션은 제외합니다. 1KB = 1,024바이트.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 8), Foreground = (Brush)FindResource("MutedTextBrush") };
        top.Children.Add(hint); top.Children.Add(_bar); top.Children.Add(_summary);
        Grid.SetRow(_log, 1); _log.Margin = new Thickness(0, 10, 0, 10); root.Children.Add(_log);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _start.Style = (Style)FindResource("PrimaryButton"); buttons.Children.Add(_open); buttons.Children.Add(_cancel); buttons.Children.Add(_start);
        var close = new Button { Content = "닫기" }; buttons.Children.Add(close); Grid.SetRow(buttons, 2); root.Children.Add(buttons); Content = root;
        _start.Click += Start_Click; _cancel.Click += (_, _) => { _cancellation?.Cancel(); _summary.Text = "취소 요청됨 · 현재 처리 정리 중"; };
        _open.Click += (_, _) => { if (_lastOutput != null && Directory.Exists(_lastOutput)) Process.Start(new ProcessStartInfo(_lastOutput) { UseShellExecute = true }); };
        close.Click += (_, _) => Close(); Closing += ClosingWindow;
        _scope.SelectionChanged += (_, _) => UpdateScope(); UpdateScope();
    }
    private void AddRow(string label, UIElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(98) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold }); Grid.SetColumn(control, 1); row.Children.Add(control); _inputs.Children.Add(row);
    }
    private static void AddNumber(Grid row, int col, string label, TextBox text)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, col < 2 ? 10 : 0, 10) }; panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 2, 0, 4), FontSize = 11 }); panel.Children.Add(text); Grid.SetColumn(panel, col); row.Children.Add(panel);
    }
    private void UpdateScope() { if (!IsRunning) _summary.Text = $"처리 예정 {_groups[Math.Max(0, _scope.SelectedIndex)].Count}개 · {_context.Title}"; }
    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (IsRunning) return;
        try
        {
            if (!int.TryParse(_size.Text, out var size) || !int.TryParse(_pixels.Text, out var pixels) || !int.TryParse(_quality.Text, out var quality)) throw new ArgumentException("용량·크기·품질은 정수로 입력하세요.");
            var options = new ResizeOptions { MaxKilobytes = size, MaxPixels = pixels, Quality = quality, LongEdge = _dimension.SelectedIndex == 1, Png = _format.SelectedIndex == 1 }; options.Validate();
            var group = _groups[_scope.SelectedIndex]; if (group.Count == 0) throw new ArgumentException("선택 범위에 이미지가 없습니다.");
            var output = Path.GetFullPath(_output.Text.Trim());
            if (MessageBox.Show(this, $"이미지 {group.Count}개를 {size}KB 이하 사본으로 만듭니다.\n\n결과 폴더:\n{output}\n\n원본 파일은 변경하지 않습니다.", "리사이즈 실행 확인", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            IsRunning = true; _inputs.IsEnabled = false; _start.IsEnabled = false; _cancel.IsEnabled = true; _open.IsEnabled = false;
            _cancellation = new CancellationTokenSource(); _log.Clear(); _bar.Value = 0; _bar.Maximum = group.Count;
            var progress = new Progress<ResizeEntry>(entry => { _bar.Value++; _log.AppendText(entry.Success ? $"완료 · {entry.SourceRelative} → {entry.Width}×{entry.Height}px / {entry.Bytes / 1024.0:0.0}KB\n" : $"제외/오류 · {entry.SourceRelative} · {entry.Error}\n"); _log.ScrollToEnd(); _summary.Text = $"처리 {_bar.Value:0} / {group.Count}"; });
            _lastOutput = output;
            Result = await Task.Run(() => ImageProcessing.ResizeBatch(_context, group, output, options, progress, _cancellation.Token));
            var ok = Result.Items.Count(i => i.Success); var fail = Result.Items.Count(i => !i.Success && i.Error != "취소됨");
            _summary.Text = $"{(Result.Cancelled ? "취소됨" : "처리 종료")} · 성공 {ok} / 실패 {fail} / 취소·미처리 {group.Count - ok - fail}";
            _open.IsEnabled = Directory.Exists(output);
        }
        catch (OperationCanceledException) { _summary.Text = "작업이 취소되었습니다."; }
        catch (Exception ex) { _summary.Text = "작업 오류"; _log.AppendText(ex.Message + "\n"); MessageBox.Show(this, ex.Message, "리사이즈 확인", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { IsRunning = false; _inputs.IsEnabled = true; _start.IsEnabled = true; _cancel.IsEnabled = false; _cancellation?.Dispose(); _cancellation = null; }
    }
    private void ClosingWindow(object? sender, CancelEventArgs e)
    {
        if (!IsRunning) return;
        _cancellation?.Cancel(); _summary.Text = "취소 처리 중입니다. 처리 종료 후 창을 닫아 주세요."; e.Cancel = true;
    }
}
