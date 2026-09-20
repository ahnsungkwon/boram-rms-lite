using System.IO;
using System.Windows;
using System.Windows.Media;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private readonly Dictionary<string, LiteEdit> _lastLiteEdits = new(StringComparer.OrdinalIgnoreCase);
    private bool _navigating, _allowClose;
    public Task LastSaveTask { get; private set; } = Task.CompletedTask;
    public Task LastNavigationTask { get; private set; } = Task.CompletedTask;
    private void UpdateDraftHint()
    {
        if (_initializing || SaveHint == null) return;
        if (!_statusDirty && _statusWrites == 0) _autoSaveError = null;
        var blankName = _editing != null && string.IsNullOrWhiteSpace(CombinedTextBox.Text);
        SaveHint.Text = blankName ? "빈칸이면 기존 파일명 유지 · ↓ 다음 / ↑ 이전" : _dirty ? "이름 저장 후 이동 · ↓ 다음 / ↑ 이전" : "↓ / Space 다음 · ↑ / Ctrl+Space 이전";
        AutoSaveStatusText.Text = _statusWrites > 0 ? "체크 상태 저장 중…" : _autoSaveError != null ? "상태 저장 안 됨 · " + _autoSaveError : _statusDirty ? "메모 작성 중 · 입력을 마치면 자동 저장" : "체크·해제 즉시 저장";
        AutoSaveStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, _autoSaveError != null && _statusWrites == 0 ? "ErrorBrush" : "AccentBrush");
        AutoSaveStatusText.ToolTip = _autoSaveError ?? "체크는 이름 저장과 별개로 바로 저장됩니다. 메모는 입력칸을 벗어나면 저장됩니다.";
        DiscardPendingButton.Visibility = Visibility.Visible;
        DiscardPendingButton.IsEnabled = _editing != null && !_busy && _statusWrites == 0 && (_dirty || _statusDirty || blankName);
        SaveStateButton.Visibility = _autoSaveError != null && _statusWrites == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHistoryHelp();
    }
    private bool HasQuotaRename => _editing != null && PreChangeCheck.IsChecked == true && !string.IsNullOrWhiteSpace(PreQuota.Text) && PreQuota.Text.Trim() != _editing.Quota;
    private void KeepExistingNameWhenBlank()
    {
        if (_editing == null || !string.IsNullOrWhiteSpace(CombinedTextBox.Text)) return;
        // Empty means no rename, never an empty filename or an implicit deletion.
        var filling = _filling; _filling = true;
        try { CombinedTextBox.Text = _editing.Key; }
        finally { _filling = filling; }
        _dirty = false;
        Log("기존 파일명 유지 · " + _editing.FileName);
        UpdateDraftHint();
    }
    private (string? Name, LiteState State) ReadDraft()
    {
        var item = _editing!;
        string? name = _dirty && !string.IsNullOrWhiteSpace(CombinedTextBox.Text) ? SafePaths.ValidName(CombinedTextBox.Text) : null;
        var state = _statusDirty ? ReadStatusState() : LiteState.From(item);
        if (HasQuotaRename)
        {
            if (!int.TryParse(PreQuota.Text.Trim(), out var quota) || quota is < 1 or > 99)
                throw new ArgumentException("'입력전변경'의 새 구좌수를 1~99로 입력하거나 해당 체크를 해제하세요.");
            var newName = name == null ? item.Name : ImageItem.Parse(name + ".tmp").Name;
            name = SafePaths.ValidName(quota + newName);
        }
        return (name, state);
    }
    public async Task<bool> TrySaveCurrentAsync()
    {
        if (_closing || _busy || _loading) return false;
        if (_active == null || _editing == null) return true;
        KeepExistingNameWhenBlank();
        if (!_dirty && !_statusDirty && _statusWrites == 0 && !HasQuotaRename) return true;
        var tab = _active; var item = _editing;
        var savingName = _dirty || HasQuotaRename;
        _busy = true;
        try
        {
            // Stop navigation until every click that preceded it has reached disk.
            await FlushStatusSavesAsync();
            if (!_dirty && !_statusDirty && !HasQuotaRename) return true;
            if (!tab.Lease.Writable) throw new IOException("읽기 전용 폴더라 저장하지 못했습니다.");
            var draft = ReadDraft(); var revision = _statusInputRevision;
            await _editSerial.WaitAsync();
            try
            {
                _ignoreWatcherUntil = DateTime.UtcNow.AddSeconds(2);
                var result = await Task.Run(() => LiteWorkspace.Save(tab.Context, item, draft.Name, draft.State));
                ApplySavedResult(tab, item, result, draft.State);
                _dirty = false;
                if (revision == _statusInputRevision) _statusDirty = false;
                _autoSaveError = null;
                _filling = true;
                try { CombinedTextBox.Text = item.Key; LoadStatus(item); }
                finally { _filling = false; }
                Log("저장 완료 · " + item.FileName);
            }
            finally { _editSerial.Release(); }
            return true;
        }
        catch (Exception ex)
        {
            var reason = LiteFileIo.Describe(ex);
            if (savingName)
                Error(new IOException("'" + item.FileName + "' 이름 저장 안 됨: " + reason + " 이름을 고치거나 빈칸으로 두고 다시 이동하세요. '입력 원래대로' 버튼도 사용할 수 있습니다."));
            else
            {
                _statusDirty = true; _autoSaveError = item.FileName + " · " + reason;
                Error(new IOException("'" + item.FileName + "' 상태 저장 안 됨: " + reason + " 체크는 유지했습니다. 표시된 '상태 다시 저장' 버튼으로 재시도하세요."));
            }
            return false;
        }
        finally { _busy = false; UpdateDraftHint(); }
    }
    public async Task SaveDraftAsync(bool next)
    {
        if (next) await NavigateImageAsync(1);
        else if (await TrySaveCurrentAsync()) { UpdateDraftHint(); FocusRenameInput(); }
    }
    public async Task NavigateImageAsync(int direction)
    {
        if (_navigating || _busy || _loading || _editing == null || direction == 0) return;
        _navigating = true;
        var items = ImageList.Items.Cast<ImageItem>().ToList();
        var index = items.IndexOf(_editing); if (index < 0) { _navigating = false; return; }
        var target = items[Math.Clamp(index + Math.Sign(direction), 0, items.Count - 1)];
        try
        {
            if (!await TrySaveCurrentAsync()) return;
            await SelectImageCoreAsync(target);
        }
        finally { _navigating = false; }
    }
    private async Task SelectImageCoreAsync(ImageItem item)
    {
        if (!AllItems.Contains(item)) return;
        _selecting = true;
        try { ImageList.SelectedItem = item; ImageList.ScrollIntoView(item); }
        finally { _selecting = false; }
        if (_editing != item)
        {
            var preview = ShowSelectionAsync(item); LastPreviewTask = preview; await preview;
        }
        UpdateDraftHint(); FocusRenameInput();
    }
    private async Task SelectImageWithSaveAsync(ImageItem? target)
    {
        if (target == null || target == _editing || _navigating || _busy || _loading) return;
        _navigating = true;
        try { if (await TrySaveCurrentAsync()) await SelectImageCoreAsync(target); }
        finally { _navigating = false; }
    }
    private void UpdateHistoryHelp()
    {
        if (_initializing || UndoLastButton == null) return;
        var edit = _active == null ? null : _lastLiteEdits.GetValueOrDefault(_active.Context.Root);
        UndoLastButton.IsEnabled = edit != null && _active?.Lease.Writable == true && !_busy && _statusWrites == 0 && !_dirty && !_statusDirty;
        var description = edit == null ? "이번 실행에서 취소할 저장이 없습니다." : edit.ImageBackup != null ? "90도 회전" : !SafePaths.Same(edit.BeforePath, edit.AfterPath) ? "이름 변경: " + Path.GetFileName(edit.BeforePath) + " → " + Path.GetFileName(edit.AfterPath) : "체크·메모 상태 저장";
        UndoLastButton.ToolTip = (edit == null ? "" : Path.GetFileName(edit.AfterPath) + "\n") + description + "\n현재 폴더의 마지막 저장 1건만 취소합니다. 파일 삭제·압축·업데이트는 취소하지 않습니다. 앱을 종료하면 이 취소 이력은 초기화됩니다.";
    }
    public async Task UndoDraftAsync()
    {
        if (_navigating || _busy || _loading || _active == null) return;
        if (!_active.Lease.Writable) { Log("읽기 전용 폴더에서는 저장된 변경을 취소할 수 없습니다."); return; }
        var context = _active.Context; _busy = true;
        try
        {
            await FlushStatusSavesAsync();
            if (_dirty || _statusDirty) { Log("현재 입력을 저장하거나 '입력 원래대로' 버튼을 누른 뒤 마지막 변경을 취소하세요."); return; }
            if (!_lastLiteEdits.TryGetValue(context.Root, out var edit)) { Log("이번 실행에서 취소할 이름·체크·메모·회전 저장이 없습니다."); return; }
            await _editSerial.WaitAsync();
            try
            {
                var path = await Task.Run(() => LiteWorkspace.Undo(context, edit));
                _previewLoader.Invalidate(edit.AfterPath);
                _previewLoader.Invalidate(path);
                _lastLiteEdits.Remove(context.Root);
                await ReloadAsync(path); await LastPreviewTask;
                Log("마지막 저장 1건을 취소했습니다 · " + Path.GetFileName(path));
            }
            finally { _editSerial.Release(); }
        }
        catch (Exception ex) { Error(ex); }
        finally { _busy = false; UpdateDraftHint(); }
    }
}
