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
        SaveHint.Text = _dirty ? "파일명 수정 중 · Space 다음 / Shift+Space 이전" : "Space 다음 · Shift+Space 이전 (이름 저장 후 이동)";
        AutoSaveStatusText.Text = _statusWrites > 0 ? "체크 상태 저장 중…" : _autoSaveError != null ? "상태 저장 실패 · 아래에서 다시 저장하세요" : _statusDirty ? "메모 작성 중 · 입력을 마치면 자동 저장" : "체크·해제 즉시 저장";
        AutoSaveStatusText.Foreground = ImageItem.Brush(_autoSaveError != null && _statusWrites == 0 ? "#AC4942" : "#457752");
        AutoSaveStatusText.ToolTip = _autoSaveError ?? "체크는 이름 저장과 별개로 바로 저장됩니다. 메모는 입력칸을 벗어나면 저장됩니다.";
        DiscardPendingButton.Visibility = _dirty || _statusDirty && _statusWrites == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHistoryHelp();
    }
    private bool HasQuotaRename => _editing != null && PreChangeCheck.IsChecked == true && PreQuota.Text.Trim() != _editing.Quota;
    private (string? Name, LiteState State) ReadDraft()
    {
        var item = _editing!;
        string? name = _dirty ? SafePaths.ValidName(CombinedTextBox.Text) : null;
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
        if (!_dirty && !_statusDirty && _statusWrites == 0 && !HasQuotaRename) return true;
        var tab = _active; var item = _editing;
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
            Error(new IOException("'" + item.FileName + "' 저장 안 됨: " + ex.Message + " 현재 입력은 유지했습니다. 수정 후 다시 저장하거나 '미저장 입력 취소'를 누르세요."));
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
            if (_dirty || _statusDirty) { Log("현재 파일에 미저장 입력이 있습니다. 저장하거나 '미저장 입력 취소' 후 마지막 변경을 취소하세요."); return; }
            if (!_lastLiteEdits.TryGetValue(context.Root, out var edit)) { Log("이번 실행에서 취소할 이름·체크·메모·회전 저장이 없습니다."); return; }
            await _editSerial.WaitAsync();
            try
            {
                var path = await Task.Run(() => LiteWorkspace.Undo(context, edit));
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
