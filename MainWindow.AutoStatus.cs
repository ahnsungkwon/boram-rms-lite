using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private readonly SemaphoreSlim _editSerial = new(1, 1);
    private long _statusInputRevision;
    private int _statusWrites;
    private string? _autoSaveError;
    public Task<bool> LastAutoSaveTask { get; private set; } = Task.FromResult(true);
    internal Func<FolderContext, ImageItem, LiteState, Task<LiteSaveResult>>? AutoSaveWriterForTests { get; set; }
    private IEnumerable<CheckBox> AllStateChecks() => _statusBoxes.Keys.Concat(RepairReasons.Children.OfType<CheckBox>()).Append(CardCheck).Append(MinorCheck);
    private IEnumerable<TextBox> StateTexts() => new[] { RepairMemo, StatusMemo, PreQuota, PostQuota };
    private void InitializeStatusAutosave()
    {
        foreach (var box in AllStateChecks()) box.Click += StateCheck_Click;
        foreach (var text in StateTexts())
        {
            text.TextChanged += (_, _) => { if (!_filling && !_initializing) MarkStatusInput(); };
            text.LostKeyboardFocus += (_, _) => { if (!_filling && !_initializing && _statusDirty && !_busy) QueueStatusSave(); };
        }
    }
    private void MarkStatusInput()
    {
        _statusInputRevision++; _statusDirty = true; UpdateDraftHint();
    }
    private void StateCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_filling || _initializing || _closing || _editing == null) return;
        // Only mutually exclusive alternatives replace one another. Repair/card/minor
        // flags are independent and are never silently dropped when another is checked.
        _filling = true;
        try
        {
            if (sender == PreCancelCheck && PreCancelCheck.IsChecked == true) PostCancelCheck.IsChecked = false;
            if (sender == PostCancelCheck && PostCancelCheck.IsChecked == true) PreCancelCheck.IsChecked = false;
            if (sender == PreChangeCheck && PreChangeCheck.IsChecked == true) PostChangeCheck.IsChecked = false;
            if (sender == PostChangeCheck && PostChangeCheck.IsChecked == true) PreChangeCheck.IsChecked = false;
        }
        finally { _filling = false; }
        MarkStatusInput(); QueueStatusSave();
        // A mouse click returns to the name field so the next Space means next image.
        // Keyboard-operated checkboxes retain the usual Space-to-toggle behavior.
        if (!TestMode && sender is CheckBox box && box.IsMouseOver) FocusRenameInput(false);
    }
    private LiteSelections ReadSelections() => new(
        string.Join(", ", RepairReasons.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => c.Content.ToString()!)),
        string.Join(" | ", _statusBoxes.Where(p => p.Key.IsChecked == true).Select(p => p.Value).Concat(MinorCheck.IsChecked == true ? new[] { "미성년자" } : Array.Empty<string>())),
        RepairMemo.Text.Trim(), StatusMemo.Text.Trim(), PreQuota.Text.Trim(), PostQuota.Text.Trim());
    private static string[] SplitFlags(string text) => text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private LiteState ReadStatusState()
    {
        var fields = ReadSelections(); var labels = new List<string>();
        if (fields.Repairs.Length > 0 || fields.RepairMemo.Length > 0) labels.Add("보완");
        labels.AddRange(SplitFlags(fields.Flags));
        var details = string.Join(", ", new[] { fields.Repairs, fields.RepairMemo, fields.Memo }.Where(s => s.Length > 0));
        return new(string.Join(" | ", labels.Distinct()), details, CardCheck.IsChecked == true, fields);
    }
    private LiteSelections SelectionsFor(ImageItem item)
    {
        if (item.Selections != null) return item.Selections;
        var flags = SplitFlags(item.Status).Where(s => s != "보완").ToList();
        var reasons = item.Details.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var known = RepairReasons.Children.OfType<CheckBox>().Select(c => c.Content.ToString()!).ToHashSet();
        var repair = item.Status.Contains("보완");
        var selected = repair ? reasons.Where(known.Contains).ToArray() : Array.Empty<string>();
        if (repair && reasons.Remove("미성년자")) flags.Add("미성년자");
        var change = System.Text.RegularExpressions.Regex.Match(item.Details, @"-->\s*(\d{1,2})\s*구좌");
        return new(string.Join(", ", selected), string.Join(" | ", flags), repair ? string.Join(", ", reasons.Where(r => !known.Contains(r))) : "", repair ? "" : item.Details,
            item.Status == "입력전 변경" && change.Success ? change.Groups[1].Value : "",
            item.Status == "입력후 변경" && change.Success ? change.Groups[1].Value : "");
    }
    private void QueueStatusSave()
    {
        if (_initializing || _filling || _closing || _editing == null || _active == null) return;
        var item = _editing; var tab = _active; var state = ReadStatusState();
        var revision = _statusInputRevision;
        var previous = LastAutoSaveTask; _statusWrites++; UpdateDraftHint();
        LastAutoSaveTask = SaveStatusAfterAsync(previous, tab, item, state, revision);
    }
    private async Task<bool> SaveStatusAfterAsync(Task<bool> previous, WorkTab tab, ImageItem item, LiteState state, long revision)
    {
        // Explicit chaining guarantees click order, including check-uncheck-check bursts.
        await Task.Yield(); await previous;
        await _editSerial.WaitAsync();
        try
        {
            if (!tab.Lease.Writable) throw new IOException("다른 Lite에서 사용 중인 읽기 전용 폴더입니다.");
            var result = TestMode && AutoSaveWriterForTests != null
                ? await AutoSaveWriterForTests(tab.Context, item, state)
                : await Task.Run(() => LiteWorkspace.Save(tab.Context, item, null, state));
            ApplySavedResult(tab, item, result, state);
            if (_active == tab && _editing == item && revision == _statusInputRevision)
            {
                _statusDirty = false; _autoSaveError = null;
                Log("상태 자동 저장 완료 · " + item.FileName);
            }
            return true;
        }
        catch (Exception ex)
        {
            if (_active == tab && _editing == item)
            {
                _statusDirty = true; _autoSaveError = item.FileName + " · " + ex.Message;
                Error(new IOException("'" + item.FileName + "' 상태 저장 실패: " + ex.Message + " 체크는 화면에 유지했습니다. '상태 다시 저장'으로 재시도하세요."));
            }
            return false;
        }
        finally { _editSerial.Release(); _statusWrites--; UpdateDraftHint(); }
    }
    public async Task<bool> FlushStatusSavesAsync()
    {
        Task<bool> task;
        do { task = LastAutoSaveTask; await task; } while (task != LastAutoSaveTask);
        return !_statusDirty;
    }
    private void ApplySavedResult(WorkTab tab, ImageItem item, LiteSaveResult result, LiteState state)
    {
        var oldPath = item.FullPath;
        item.FullPath = result.Path; item.RelativePath = Path.GetRelativePath(tab.Context.Root, result.Path);
        (item.Quota, item.Name) = ImageItem.Parse(item.FileName);
        item.Status = state.Status; item.Details = state.Details; item.Card = state.Card; item.Selections = state.Selections;
        if (result.Edit != null)
        {
            item.LiteRevision = result.Edit.AfterStateRevision;
            _lastLiteEdits[tab.Context.Root] = result.Edit;
        }
        item.StateNotice = "";
        if (tab.Order.Remove(oldPath, out var order)) tab.Order[result.Path] = order;
        if (tab.SelectedPath == oldPath) tab.SelectedPath = result.Path;
        if (_active != tab) return;
        var selecting = _selecting; _selecting = true;
        try { ImageList.Items.Refresh(); } finally { _selecting = selecting; }
        if (_editing == item)
        {
            CurrentFileText.Text = item.RelativePath; SelectedFileText.Text = item.FileName;
            ImageDetails.Text = PreviewImage.Source is BitmapSource image
                ? $"{image.PixelWidth} × {image.PixelHeight}px · {item.Length / 1024.0:0}KB · {item.StatusLabel}"
                : item.StatusLabel;
        }
        UpdateSummary(); UpdateHistoryHelp();
    }
    private async void RetryState_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _loading || _editing == null) return;
        MarkStatusInput(); QueueStatusSave(); await LastAutoSaveTask;
    }
    private async void ClearAllStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _loading || _editing == null) return;
        _filling = true;
        try { foreach (var box in AllStateChecks()) box.IsChecked = false; foreach (var text in StateTexts()) text.Clear(); }
        finally { _filling = false; }
        MarkStatusInput(); QueueStatusSave(); await LastAutoSaveTask;
    }
    public async Task DiscardPendingInputAsync()
    {
        if (_busy || _loading || _navigating || _editing == null || _active == null) return;
        var item = _editing; var tab = _active; _busy = true;
        try
        {
            await FlushStatusSavesAsync();
            // Explicit user action only: reload saved state, never undo a completed autosave.
            var latest = await Task.Run(() => LiteWorkspace.Load(tab.Context).FirstOrDefault(i => SafePaths.Same(i.FullPath, item.FullPath)));
            if (latest == null) throw new IOException("현재 이미지가 없어 입력을 다시 읽지 못했습니다.");
            var index = AllItems.IndexOf(item); if (index >= 0) AllItems[index] = latest;
            _dirty = _statusDirty = false; _autoSaveError = null;
            Rebind(latest.FullPath); await LastPreviewTask;
            Log("미저장 입력만 취소했습니다. 이미 저장한 이름·체크 상태는 유지합니다.");
        }
        catch (Exception ex) { Error(ex); }
        finally { _busy = false; UpdateDraftHint(); }
    }
    private async void DiscardPending_Click(object sender, RoutedEventArgs e) => await DiscardPendingInputAsync();
}
