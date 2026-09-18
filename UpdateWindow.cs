using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace BoramRms.Lite;
public sealed class UpdateWindow : Window
{
    private readonly TextBlock _state = new() { Text = "새 버전을 확인합니다.", FontSize = 17, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,10) };
    private readonly TextBox _notes = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ProgressBar _progress = new() { Height = 8, Margin = new Thickness(0,10,0,10), Maximum = 100 };
    private readonly PasswordBox _password = new() { Height = 32, Margin = new Thickness(0,8,0,8) };
    private readonly Button _check = new() { Content = "다시 확인" }, _install = new() { Content = "업데이트 설치", IsEnabled = false }, _cancel = new() { Content = "닫기" };
    private readonly StackPanel _auth = new();
    private CancellationTokenSource? _cancelSource;
    private UpdateRelease? _release;
    private bool _running, _installing;
    public UpdatePlan? Prepared { get; private set; }
    public UpdateWindow(bool testMode = false)
    {
        ThemeManager.BindWindow(this);
        Title = "보람 RMS Lite · 업데이트"; Width = 650; Height = 650; MinWidth = 560; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Malgun Gothic"); FontSize = 12;
        var grid = new Grid { Margin = new Thickness(22) };
        grid.SetResourceReference(Grid.BackgroundProperty, "AppBg");
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel(); top.Children.Add(new TextBlock { Text = "보람 RMS Lite 업데이트", FontSize = 23, FontWeight = FontWeights.Bold });
        top.Children.Add(new TextBlock { Text = "현재 " + UpdateIdentity.CurrentVersion + "  ·  " + UpdateIdentity.Repository, Margin = new Thickness(0,6,0,0) }); top.Children.Add(_state); top.Children.Add(_progress); grid.Children.Add(top);
        Grid.SetRow(_notes, 1); grid.Children.Add(_notes);
        var bottom = new StackPanel { Margin = new Thickness(0,12,0,0) };
        var expander = new Expander { Header = "GitHub 인증 설정 · 비공개 저장소" };
        _auth.Children.Add(new TextBlock { Text = "이 PC에 로그인한 GitHub CLI를 자동으로 사용합니다. CLI가 없는 PC에서는 이 저장소의 Contents: Read 권한만 가진 개인 토큰을 입력하세요. 토큰은 현재 Windows 사용자용으로 암호화하며 배포 ZIP이나 GitHub 소스에 포함하지 않습니다.", TextWrapping = TextWrapping.Wrap });
        _auth.Children.Add(_password); var authButtons = new WrapPanel();
        var save = new Button { Content = "토큰 저장" }; var clear = new Button { Content = "저장 토큰 지우기" }; var open = new Button { Content = "GitHub 저장소" };
        authButtons.Children.Add(save); authButtons.Children.Add(clear); authButtons.Children.Add(open); _auth.Children.Add(authButtons); expander.Content = _auth; bottom.Children.Add(expander);
        save.Click += async (_, _) => { try { UpdateCredential.Save(_password.Password); _password.Clear(); await CheckAsync(); } catch (Exception ex) { _state.Text = ex.Message; } };
        clear.Click += async (_, _) => { UpdateCredential.Forget(); _password.Clear(); await CheckAsync(); };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo("https://github.com/" + UpdateIdentity.Repository) { UseShellExecute = true });
        bottom.Children.Add(new TextBlock { Text = "설치 시 Lite가 종료된 뒤 새 버전으로 다시 열립니다. 신청서 폴더와 사용자 설정은 교체하지 않습니다. 이전 앱 폴더는 백업으로 보관합니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,8), FontSize = 11 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _install.Style = (Style)FindResource("PrimaryButton"); buttons.Children.Add(_check); buttons.Children.Add(_install); buttons.Children.Add(_cancel); bottom.Children.Add(buttons); Grid.SetRow(bottom, 2); grid.Children.Add(bottom); Content = grid;
        _check.Click += async (_, _) => await CheckAsync(); _install.Click += async (_, _) => await InstallAsync();
        _cancel.Click += (_, _) => { if (_running) _cancelSource?.Cancel(); else Close(); };
        if (!testMode) Loaded += async (_, _) => await CheckAsync(); Closing += OnClosing;
    }
    private void SetRunning(bool value)
    {
        _running = value; _check.IsEnabled = !value; _auth.IsEnabled = !value; _install.IsEnabled = !value && _release?.IsNewer == true; _cancel.Content = value ? "취소" : "닫기";
    }
    private async Task CheckAsync()
    {
        if (_running) return; _release = null; SetRunning(true); _state.Text = "GitHub 배포 버전 확인 중…";
        _cancelSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var client = new GitHubUpdateClient(await UpdateCredential.ResolveAsync(_cancelSource.Token));
            _release = await client.LatestAsync(_cancelSource.Token);
            _state.Text = _release.IsNewer ? "새 버전 " + _release.Manifest.Version + "을 설치할 수 있습니다." : "최신 버전을 사용 중입니다. (" + UpdateIdentity.CurrentVersion + ")";
            _notes.Text = "배포 버전: " + _release.Manifest.Version + "\n배포 시각: " + _release.PublishedAt + "\n크기: " + (_release.Manifest.Size / 1024.0 / 1024).ToString("0.0") + "MB\n\n" + _release.Notes;
        }
        catch (OperationCanceledException) { _state.Text = "확인이 취소되었거나 시간이 초과되었습니다."; }
        catch (Exception ex) { _state.Text = "업데이트 확인을 완료하지 못했습니다."; _notes.Text = ex.Message; }
        finally { SetRunning(false); _cancelSource.Dispose(); _cancelSource = null; }
    }
    private async Task InstallAsync()
    {
        if (_running || _release?.IsNewer != true) return;
        if (MessageBox.Show(this, "버전 " + _release.Manifest.Version + "을 내려받아 검증한 뒤 Lite를 종료·교체·재실행할까요?", "업데이트 설치", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetRunning(true); _installing = true; _cancelSource = new CancellationTokenSource();
        try
        {
            using var client = new GitHubUpdateClient(await UpdateCredential.ResolveAsync(_cancelSource.Token));
            _state.Text = "다운로드 및 무결성 확인 중…";
            var progress = new Progress<double>(p => { _progress.Value = p; _state.Text = p < 100 ? $"다운로드 {p:0}%" : "다운로드 완료 · 파일 검증 중…"; });
            Prepared = await UpdatePackage.PrepareAsync(client, _release, AppContext.BaseDirectory, progress, _cancelSource.Token);
            SetRunning(false); DialogResult = true;
        }
        catch (OperationCanceledException) { _state.Text = "설치를 취소했습니다. 기존 버전은 그대로입니다."; }
        catch (Exception ex) { _state.Text = "설치 준비를 완료하지 못했습니다."; _notes.Text = ex.Message; }
        finally { _installing = false; SetRunning(false); _cancelSource.Dispose(); _cancelSource = null; }
    }
    private void OnClosing(object? s, CancelEventArgs e) { if (!_running) return; _cancelSource?.Cancel(); e.Cancel = true; _state.Text = _installing ? "설치 준비를 취소하는 중입니다." : "확인을 취소하는 중입니다."; }
}
