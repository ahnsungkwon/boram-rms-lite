using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
namespace BoramRms.Lite;
public partial class MainWindow : Window
{
    public ObservableCollection<WorkTab> Tabs { get; } = new();
    public List<ImageItem> AllItems { get; private set; } = new();
    public Task LastPreviewTask { get; private set; } = Task.CompletedTask;
    private WorkTab? _active;
    private ImageItem? _editing;
    private bool _initializing = true, _selecting, _filling, _dirty, _statusDirty, _loading, _closing, _thumbnails;
    private bool _busyValue;
    private bool _busy
    {
        get => _busyValue;
        set { _busyValue = value; if (IsInitialized) { WorkArea.IsEnabled = !value; WorkTabs.IsEnabled = !value; foreach (var panel in _docks.Values) panel.IsEnabled = !value; } }
    }
    public bool HasPendingRename => _dirty;
    public bool HasPendingStatus => _statusDirty;
    private int _loadGeneration, _previewGeneration;
    private CancellationTokenSource? _loadCancellation, _thumbnailCancellation;
    private FileSystemWatcher? _watcher;
    private DateTime _ignoreWatcherUntil;
    private Point? _drag;
    private Point _dragOrigin;
    private readonly SavedSettings _previousSettings;
    private readonly Dictionary<string, PanelWindow> _docks = new();
    private readonly Dictionary<CheckBox,string> _statusBoxes;
    public bool TestMode { get; }
    public MainWindow(bool testMode = false)
    {
        TestMode = testMode;
        ThemeManager.EnsureLoaded();
        InitializeComponent();
        _previousSettings = SettingsStore.Load();
        Width = Math.Clamp(_previousSettings.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Clamp(_previousSettings.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        LeftColumn.Width = new GridLength(Math.Clamp(_previousSettings.LeftWidth, 270, 480));
        RightColumn.Width = new GridLength(Math.Clamp(_previousSettings.RightWidth, 235, 500));
        _thumbnails = _previousSettings.Thumbnails;
        WorkTabs.ItemsSource = Tabs;
        StatusFilter.ItemsSource = new[] { "전체", "CMS", "카드", "보완", "주말미등록", "취소", "입력전 변경", "입력후 변경", "오기입", "오등록", "추가" };
        StatusFilter.SelectedIndex = 0; SortMode.SelectedIndex = 0;
        _statusBoxes = new() { [WeekendCheck] = "주말미등록", [AdditionalCheck] = "추가", [PreCancelCheck] = "입력전 취소", [PostCancelCheck] = "입력후 취소", [MistakeCheck] = "오기입", [WrongCheck] = "오등록", [PreChangeCheck] = "입력전 변경", [PostChangeCheck] = "입력후 변경" };
        InitializeStatusAutosave();
        _initializing = false;
        SetTemplate(); UpdateSummary(); InitializeNavigation(); InitializeUpdater(); UpdateDraftHint(); InitializeThemes();
    }
    private void Log(string text)
    {
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); StatusText.Text = text; StatusText.ToolTip = text; LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\n");
        if (LogBox.Text.Length > 20000) LogBox.Text = LogBox.Text[^16000..]; LogBox.ScrollToEnd();
    }
    private void Error(Exception ex) { Log("처리하지 못했습니다 · " + ex.Message); StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush"); StatusText.ToolTip = ex.Message; }
    private bool Writable()
    {
        if (_busy || _loading) { Log("현재 처리가 끝난 뒤 다시 시도하세요."); return false; }
        if (_active == null) { Log("폴더를 먼저 선택하세요."); return false; }
        if (!_active.Lease.Writable) { Log("다른 Lite에서 사용 중인 폴더입니다. 읽기 전용으로 열렸습니다."); return false; }
        return true;
    }
    public async Task OpenFolderAsync(string path)
    {
        if (_busy || _navigating || !await TrySaveCurrentAsync()) return;
        try
        {
            var ctx = FolderContext.Resolve(path);
            var existing = Tabs.FirstOrDefault(t => SafePaths.Same(t.Context.Root, ctx.Root));
            if (existing == null) { existing = new WorkTab(ctx); Tabs.Add(existing); }
            _selecting = true; WorkTabs.SelectedItem = existing; _selecting = false;
            _active = existing; _dirty = _statusDirty = false; await ReloadAsync(existing.SelectedPath);
            FocusRenameInput();
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new OpenFolderDialog { Title = "강연회 폴더 / 신청서 [원본] / 이미지 폴더 선택" };
        if (dlg.ShowDialog(this) != true) return;
        var root = dlg.FolderName;
        try
        {
            var children = Directory.EnumerateDirectories(root).Where(d => Directory.Exists(Path.Combine(d,"신청서","[원본]"))).ToList();
            if (children.Count > 1)
            {
                if (MessageBox.Show(this, $"하위 강연회 {children.Count}개를 각각 탭으로 열까요?", "여러 폴더", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                foreach (var dir in children) await OpenFolderAsync(dir);
            }
            else if (children.Count == 1 && !Directory.Exists(Path.Combine(root,"신청서","[원본]"))) await OpenFolderAsync(children[0]);
            else await OpenFolderAsync(root);
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void WorkTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _selecting || e.Source != WorkTabs) return;
        var requested = WorkTabs.SelectedItem as WorkTab;
        _selecting = true; WorkTabs.SelectedItem = _active; _selecting = false;
        LastNavigationTask = SelectTabWithSaveAsync(requested); await LastNavigationTask;
    }
    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkTab tab }) await CloseTabAsync(tab);
    }
    public async Task CloseTabAsync(WorkTab tab)
    {
        if (_busy || _navigating || !Tabs.Contains(tab)) return;
        if (tab == _active && !await TrySaveCurrentAsync()) return;
        var wasActive = tab == _active; var index = Tabs.IndexOf(tab);
        _selecting = true;
        try
        {
            Tabs.Remove(tab); tab.Dispose();
            if (wasActive) _active = Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : null;
            WorkTabs.SelectedItem = _active;
        }
        finally { _selecting = false; }
        if (wasActive) await ReloadAsync(_active?.SelectedPath);
    }
    private void PreviousTab_Click(object s, RoutedEventArgs e) { if (Tabs.Count > 0) WorkTabs.SelectedIndex = (WorkTabs.SelectedIndex - 1 + Tabs.Count) % Tabs.Count; }
    private void NextTab_Click(object s, RoutedEventArgs e) { if (Tabs.Count > 0) WorkTabs.SelectedIndex = (WorkTabs.SelectedIndex + 1) % Tabs.Count; }
    private async void RestoreSession_Click(object s, RoutedEventArgs e)
    {
        LastNavigationTask = RestorePreviousFoldersAsync(); await LastNavigationTask;
    }
    public async Task ReloadAsync(string? preferred = null)
    {
        _loadCancellation?.Cancel(); _thumbnailCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); _loadCancellation = cancellation;
        var version = ++_loadGeneration; ++_previewGeneration; var tab = _active;
        _editing = null; _dirty = _statusDirty = false; _loading = true;
        _watcher?.Dispose(); _watcher = null;
        try
        {
            if (tab == null) { AllItems = new(); Rebind(); ClearSelection(); return; }
            Log("이미지 목록을 읽는 중입니다.");
            var data = await Task.Run(() => LiteWorkspace.Load(tab.Context, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (version != _loadGeneration || _closing) return;
            AllItems = data;
            foreach (var item in data.OrderBy(i => File.GetCreationTimeUtc(i.FullPath)).ThenBy(i => i.FileName))
                if (!tab.Order.ContainsKey(item.FullPath)) tab.Order[item.FullPath] = tab.Order.Count;
            OriginalDirText.Text = tab.Context.Root;
            Rebind(preferred ?? tab.SelectedPath);
            Log($"{data.Count}개 이미지 · " + (tab.Lease.Writable ? "작업 준비" : "읽기 전용"));
            _watcher = new FileSystemWatcher(tab.Context.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
            _watcher.Changed += WatchChanged; _watcher.Created += WatchChanged; _watcher.Deleted += WatchChanged; _watcher.Renamed += WatchChanged;
            if (_thumbnails) await LoadThumbnailsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _loadGeneration) { AllItems = new(); Rebind(); Error(ex); } }
        finally { if (version == _loadGeneration) _loading = false; }
    }
    private void WatchChanged(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath.Split(Path.DirectorySeparatorChar).Any(p => p is ".boramrms" or ".rmslite") || DateTime.UtcNow < _ignoreWatcherUntil) return;
        Dispatcher.BeginInvoke(() => { if (!_closing && !_busy) StatusText.Text = "폴더 변경 감지 · 입력을 저장한 뒤 F5로 새로고침하세요."; });
    }
    public void Rebind(string? preferred = null)
    {
        if (_initializing || (_dirty || _statusDirty) && !_busy) return;
        preferred ??= (ImageList.SelectedItem as ImageItem)?.FullPath;
        var keyword = SearchText.Text.Trim(); var status = StatusFilter.SelectedItem as string ?? "전체";
        IEnumerable<ImageItem> query = AllItems.Where(i => keyword.Length == 0 || i.FileName.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) || i.Details.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
        query = query.Where(i => status switch { "전체" => true, "CMS" => !i.Card, "카드" => i.Card, _ => i.Status.Contains(status) });
        query = SortMode.SelectedIndex switch { 1 => query.OrderBy(i => int.TryParse(i.Quota, out var n) ? n : 1000).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase), 2 => query.OrderBy(i => i.FileName, StringComparer.CurrentCultureIgnoreCase), _ => query.OrderBy(i => _active?.Order.GetValueOrDefault(i.FullPath) ?? i.Index) };
        var visible = query.ToList(); _selecting = true;
        ImageList.ItemsSource = visible; ImageList.SelectedItem = visible.FirstOrDefault(i => preferred != null && SafePaths.Same(i.FullPath, preferred)) ?? visible.FirstOrDefault(); _selecting = false;
        LastPreviewTask = ShowSelectionAsync(ImageList.SelectedItem as ImageItem); UpdateSummary();
    }
    private async void Filter_Changed(object s, SelectionChangedEventArgs e) { if (!_initializing) { LastNavigationTask = ApplyFilterWithSaveAsync(); await LastNavigationTask; } }
    private async void Search_Changed(object s, TextChangedEventArgs e) { if (!_initializing) { LastNavigationTask = ApplyFilterWithSaveAsync(); await LastNavigationTask; } }
    private async void ClearFilter_Click(object s, RoutedEventArgs e)
    {
        if (_initializing || _busy || _loading || _navigating) return;
        _initializing = true;
        try { SearchText.Text = ""; StatusFilter.SelectedIndex = 0; }
        finally { _initializing = false; }
        LastNavigationTask = ApplyFilterWithSaveAsync(); await LastNavigationTask;
    }
    private async void Images_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_initializing || _selecting) return;
        var requested = ImageList.SelectedItem as ImageItem;
        if (requested == _editing) return;
        _selecting = true; ImageList.SelectedItem = _editing; _selecting = false;
        LastNavigationTask = SelectImageWithSaveAsync(requested); LastPreviewTask = LastNavigationTask; await LastNavigationTask;
    }
    private void ClearSelection()
    {
        _editing = null; _filling = true; CombinedTextBox.Text = "";
        foreach (var box in _statusBoxes.Keys.Concat(RepairReasons.Children.OfType<CheckBox>()).Append(CardCheck).Append(MinorCheck)) box.IsChecked = false;
        foreach (var text in new[] { RepairMemo, StatusMemo, PreQuota, PostQuota }) text.Text = "";
        NewFileText.Text = "—"; ImageError.Text = "";
        if (_active == null) OriginalDirText.Text = "선택된 폴더 없음";
        _filling = false; _dirty = _statusDirty = false; _autoSaveError = null; UpdateDraftHint();
        PreviewImage.Source = null; EmptyHint.Visibility = Visibility.Visible; SelectedFileText.Text = "선택된 이미지 없음"; CurrentFileText.Text = "—"; ImageDetails.Text = "상태는 Lite에만 저장합니다"; UpdateSummary();
    }
    private async Task ShowSelectionAsync(ImageItem? item)
    {
        var generation = ++_previewGeneration;
        if (item == null) { ClearSelection(); return; }
        _editing = item; _filling = true;
        CombinedTextBox.Text = item.Key; CurrentFileText.Text = item.RelativePath; SelectedFileText.Text = item.FileName;
        LoadStatus(item); _filling = false; _dirty = _statusDirty = false; _autoSaveError = null; UpdateDraftHint();
        if (item.StateNotice.Length > 0) Log(item.StateNotice);
        if (_active != null) _active.SelectedPath = item.FullPath;
        PreviewImage.Source = null; ImageError.Text = ""; EmptyHint.Visibility = Visibility.Collapsed;
        ImageDetails.Text = item.StatusLabel + " · Lite 상태";
        UpdateSummary();
        try
        {
            var source = await Task.Run(() => ImageProcessing.Load(item.FullPath, 3200));
            if (generation != _previewGeneration || _closing) return;
            PreviewImage.Source = source;
            ApplyImageViewport(source);
            ImageDetails.Text = $"{source.PixelWidth} × {source.PixelHeight}px · {item.Length / 1024.0:0}KB · {item.StatusLabel} · Lite 상태";
        }
        catch (Exception ex) { if (generation == _previewGeneration) { EmptyHint.Visibility = Visibility.Visible; ImageError.Text = ex.Message; } }
    }
    private void LoadStatus(ImageItem item)
    {
        var fields = SelectionsFor(item); var flags = SplitFlags(fields.Flags).ToHashSet();
        foreach (var kv in _statusBoxes) kv.Key.IsChecked = flags.Contains(kv.Value);
        var repairs = fields.Repairs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (var c in RepairReasons.Children.OfType<CheckBox>()) c.IsChecked = repairs.Contains(c.Content.ToString()!);
        CardCheck.IsChecked = item.Card; MinorCheck.IsChecked = flags.Contains("미성년자");
        RepairMemo.Text = fields.RepairMemo; StatusMemo.Text = fields.Memo;
        PreQuota.Text = fields.PreQuota; PostQuota.Text = fields.PostQuota;
    }
    private void Rename_TextChanged(object s, TextChangedEventArgs e)
    {
        if (_initializing || NewFileText == null) return;
        NewFileText.Text = _editing != null && string.IsNullOrWhiteSpace(CombinedTextBox.Text)
            ? "기존 파일명 유지 · " + _editing.FileName
            : SafePaths.NormalizeQuotaName(CombinedTextBox.Text) + (_editing == null ? "" : Path.GetExtension(_editing.FileName));
        if (!_filling) { _dirty = _editing != null && SafePaths.NormalizeQuotaName(CombinedTextBox.Text) != _editing.Key; UpdateDraftHint(); }
    }
    private async void SaveNext_Click(object s, RoutedEventArgs e) => await SaveRenameAsync(true);
    private async void SaveOnly_Click(object s, RoutedEventArgs e) => await SaveRenameAsync(false);
    public Task SaveRenameAsync(bool next) => LastSaveTask = SaveDraftAsync(next);
    public void MoveImage(int offset)
    {
        LastNavigationTask = NavigateImageAsync(offset); LastPreviewTask = LastNavigationTask;
    }
    private void NextImage_Click(object s, RoutedEventArgs e) => MoveImage(1);
    private void PreviousImage_Click(object s, RoutedEventArgs e) => MoveImage(-1);
    private void UpdateSummary()
    {
        if (_initializing) return;
        StatusSummary.Text = $"전체 {AllItems.Count} / 표시 {ImageList.Items.Count}";
        LocalSummary.Text = $"CMS {AllItems.Count(i => !i.Card)} · 카드 {AllItems.Count(i => i.Card)} · 보완 {AllItems.Count(i => i.Status.Contains("보완"))} · 취소 {AllItems.Count(i => i.Status.Contains("취소"))}";
        var index = ImageList.SelectedIndex + 1;
        ImagePositionText.Text = $"{index} / {ImageList.Items.Count}"; ProgressSummary.Text = $"검수 위치 {index} / {ImageList.Items.Count} · 선택 위치 기준 (완료 기록 아님)";
        PreviousImageRail.IsEnabled = index > 1;
        NextImageRail.IsEnabled = index > 0 && index < ImageList.Items.Count;
        CompressOriginalsButton.IsEnabled = _active?.Lease.Writable == true && ImageList.Items.Count > 0;
        QuotaProgress.Children.Clear();
        for (int n = 1; n <= 6; n++)
        {
            var visible = ImageList.Items.Cast<ImageItem>().ToList();
            bool InBucket(ImageItem item) => int.TryParse(item.Quota, out var q) && (n < 6 ? q == n : q >= n);
            var total = visible.Count(InBucket); var done = visible.Take(index).Count(InBucket);
            var panel = new StackPanel { Margin = new Thickness(3,0,3,0) };
            panel.Children.Add(new TextBlock { Text = $"{n}{(n == 6 ? "+" : "")}구좌 {done}/{total}", FontSize = 10 });
            panel.Children.Add(new ProgressBar { Height = 4, Margin = new Thickness(0,4,0,0), Maximum = Math.Max(1,total), Value = done }); QuotaProgress.Children.Add(panel);
        }
    }
    private async void Thumbnails_Click(object s, RoutedEventArgs e) { _thumbnails = !_thumbnails; SetTemplate(); if (_thumbnails) await LoadThumbnailsAsync(); }
    private void SetTemplate() { ImageList.ItemTemplate = (DataTemplate)Resources[_thumbnails ? "ThumbnailTemplate" : "RowTemplate"]; ThumbnailsButton.Content = _thumbnails ? "☰" : "▦"; }
    private async Task LoadThumbnailsAsync()
    {
        _thumbnailCancellation?.Cancel(); var c = new CancellationTokenSource(); _thumbnailCancellation = c;
        var data = AllItems.ToArray();
        try { foreach (var item in data) { c.Token.ThrowIfCancellationRequested(); if (item.Thumbnail != null) continue; try { item.Thumbnail = await Task.Run(() => ImageProcessing.Load(item.FullPath, 140), c.Token); } catch (OperationCanceledException) { throw; } catch { } } if (!c.IsCancellationRequested && !_closing) ImageList.Items.Refresh(); } catch (OperationCanceledException) { }
    }
    private void Zoom(double factor) { if (_active != null) SetViewport(ImageScale.ScaleX * factor, ImageTranslate.X, ImageTranslate.Y); }
    private void Fit() { if (_active != null) { _active.Camera.BaseWidth = 0; SetViewport(1, 0, 0); if (PreviewImage.Source is BitmapSource image) ApplyImageViewport(image); } }
    private void ZoomIn_Click(object s, RoutedEventArgs e) => Zoom(1.2);
    private void ZoomOut_Click(object s, RoutedEventArgs e) => Zoom(1 / 1.2);
    private void Fit_Click(object s, RoutedEventArgs e) => Fit();
    private void Image_Wheel(object s, MouseWheelEventArgs e) { Zoom(e.Delta > 0 ? 1.12 : 1 / 1.12); e.Handled = true; }
    private void Image_Down(object s, MouseButtonEventArgs e) { if (_busy || _loading) return; FocusRenameInput(false); if (e.ClickCount == 3) { Fit(); e.Handled = true; return; } _drag = e.GetPosition(ImageStage); _dragOrigin = new Point(ImageTranslate.X, ImageTranslate.Y); ImageStage.CaptureMouse(); e.Handled = true; }
    private void Image_Move(object s, MouseEventArgs e) { if (_drag.HasValue && e.LeftButton == MouseButtonState.Pressed) { var p = e.GetPosition(ImageStage); SetViewport(ImageScale.ScaleX, _dragOrigin.X + p.X - _drag.Value.X, _dragOrigin.Y + p.Y - _drag.Value.Y); } }
    private void Image_Up(object s, MouseButtonEventArgs e) { _drag = null; ImageStage.ReleaseMouseCapture(); }
    private async void RotateLeft_Click(object s, RoutedEventArgs e) => await RotateAsync(false);
    private async void RotateRight_Click(object s, RoutedEventArgs e) => await RotateAsync(true);
    private async Task RotateAsync(bool clockwise)
    {
        if (!Writable() || _editing == null || _active == null || !await TrySaveCurrentAsync()) return;
        var item = _editing; var ctx = _active.Context; _busy = true; _ignoreWatcherUntil = DateTime.UtcNow.AddSeconds(2);
        try { _lastLiteEdits[ctx.Root] = await Task.Run(() => LiteWorkspace.Rotate(ctx, item, clockwise)); await ReloadAsync(item.FullPath); Log("90도 회전 저장 · 이번 실행의 되돌리기로 복구할 수 있습니다."); } catch (Exception ex) { Error(ex); } finally { _busy = false; }
    }
    private async void Undo_Click(object s, RoutedEventArgs e) => await UndoDraftAsync();
    private void OpenExplorer_Click(object s, RoutedEventArgs e) { if (_active != null) Process.Start(new ProcessStartInfo(_active.Context.Root) { UseShellExecute = true }); }
    private async void Refresh_Click(object s, RoutedEventArgs e) { if (!_busy && await TrySaveCurrentAsync()) await ReloadAsync(_active?.SelectedPath); }
    private async void Window_Closing(object? s, CancelEventArgs e)
    {
        if (!_allowClose && (_busy || _loading || _navigating)) { e.Cancel = true; Log("저장 또는 이동 중입니다. 완료 후 창을 닫아 주세요."); return; }
        if (!_allowClose && (_dirty || _statusDirty || _statusWrites > 0 || HasQuotaRename))
        {
            e.Cancel = true;
            if (await TrySaveCurrentAsync()) { _allowClose = true; _ = Dispatcher.BeginInvoke(new Action(Close)); }
            return;
        }
        _closing = true; _loadCancellation?.Cancel(); _thumbnailCancellation?.Cancel(); _watcher?.Dispose();
        foreach (var w in _docks.Values.ToArray()) w.Close();
        try { SettingsStore.Save(new SavedSettings { Width = RestoreBounds.Width > 0 ? RestoreBounds.Width : Width, Height = RestoreBounds.Height > 0 ? RestoreBounds.Height : Height, LeftWidth = LeftColumn.ActualWidth, RightWidth = RightColumn.ActualWidth, Folders = Tabs.Select(t => t.Context.Root).ToList(), Selected = Tabs.Where(t => t.SelectedPath != null).ToDictionary(t => t.Context.Root, t => t.SelectedPath!), Thumbnails = _thumbnails }); } catch { }
        foreach (var tab in Tabs) tab.Dispose();
    }
    private void DetachLeft_Click(object s, RoutedEventArgs e) => ToggleDock("rename", LeftHost, "리네임", 360);
    private void DetachImage_Click(object s, RoutedEventArgs e) => ToggleDock("image", ImageHost, "신청서 이미지", 760);
    private void DetachRight_Click(object s, RoutedEventArgs e) => ToggleDock("list", RightHost, "신청서 목록", 340);
    public void ToggleDock(string key, ContentControl host, string title, double width)
    {
        if (_busy) return;
        if (_docks.TryGetValue(key, out var existing)) { existing.Close(); return; }
        var content = host.Content as UIElement; if (content == null) return;
        host.Content = null;
        var window = new PanelWindow(title, content, width) { Owner = this };
        _docks[key] = window;
        var restore = new Button { Content = title + " 복귀", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        restore.Click += (_, _) => window.Close(); host.Content = restore;
        window.Closed += (_, _) => { window.ReleaseContent(); host.Content = content; _docks.Remove(key); };
        window.PreviewKeyDown += Window_KeyDown;
        window.PreviewKeyUp += Window_KeyUp;
        window.Show();
    }
    private void ResetLayout_Click(object s, RoutedEventArgs e) { if (_busy || _loading) return; foreach (var w in _docks.Values.ToArray()) w.Close(); LeftColumn.Width = new GridLength(316); RightColumn.Width = new GridLength(288); Fit(); }

}
