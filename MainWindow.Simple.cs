using System.IO;
using System.Windows.Controls;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private readonly Dictionary<string, LiteEdit> _lastLiteEdits = new(StringComparer.OrdinalIgnoreCase);
    public Task LastSaveTask { get; private set; } = Task.CompletedTask;
    private void UpdateDraftHint()
    {
        if (_initializing || SaveHint == null) return;
        SaveHint.Text = (_dirty || _statusDirty) ? "수정 중 · 이름과 상태를 함께 저장합니다" : "이름·상태 수정 후 저장하고 다음";
    }
    private (string? Name, LiteState State) ReadDraft()
    {
        var item = _editing!;
        string? name = _dirty ? SafePaths.ValidName(CombinedTextBox.Text) : null;
        if (!_statusDirty) return (name, LiteState.From(item));
        var states = _statusBoxes.Where(p => p.Key.IsChecked == true).Select(p => p.Value).ToList();
        var repairs = RepairReasons.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => c.Content.ToString()!).ToList();
        if (MinorCheck.IsChecked == true) repairs.Add("미성년자");
        if (RepairMemo.Text.Trim().Length > 0) repairs.Add(RepairMemo.Text.Trim());
        if (repairs.Count > 0) states.Add("보완");
        if (states.Count > 1) throw new ArgumentException("보완 또는 기타 상태 하나를 선택하세요. 보완 사유는 여러 개 선택할 수 있습니다.");
        var state = states.FirstOrDefault() ?? "";
        var details = state == "보완" ? string.Join(", ", repairs) : StatusMemo.Text.Trim();
        if (state is "입력전 변경" or "입력후 변경")
        {
            var value = state == "입력전 변경" ? PreQuota.Text : PostQuota.Text;
            if (!int.TryParse(value.Trim(), out var quota) || quota is < 1 or > 99 || item.Quota.Length == 0)
                throw new ArgumentException("변경 구좌수를 1~99 사이로 입력하세요.");
            var newName = name == null ? item.Name : ImageItem.Parse(name + ".tmp").Name;
            details = $"{item.Quota}구좌-->{quota}구좌";
            if (StatusMemo.Text.Trim().Length > 0 && !StatusMemo.Text.Contains("구좌-->")) details += " / " + StatusMemo.Text.Trim();
            if (state == "입력전 변경") name = SafePaths.ValidName(quota + newName);
        }
        return (name, new LiteState(state, details, CardCheck.IsChecked == true));
    }
    public async Task SaveDraftAsync(bool next)
    {
        if (!Writable() || _editing == null || _active == null) return;
        if (!_dirty && !_statusDirty) { if (next) MoveImage(1); else Log("변경한 내용이 없습니다."); return; }
        var tab = _active; var item = _editing;
        try
        {
            var draft = ReadDraft();
            var visible = ImageList.Items.Cast<ImageItem>().ToList();
            var nextPath = visible.Skip(visible.IndexOf(item) + 1).FirstOrDefault()?.FullPath;
            _busy = true; _ignoreWatcherUntil = DateTime.UtcNow.AddSeconds(2);
            var result = await Task.Run(() => LiteWorkspace.Save(tab.Context, item, draft.Name, draft.State));
            if (result.Edit != null) _lastLiteEdits[tab.Context.Root] = result.Edit;
            if (tab.Order.Remove(item.FullPath, out var order)) tab.Order[result.Path] = order;
            _dirty = _statusDirty = false;
            await ReloadAsync(next && nextPath != null ? nextPath : result.Path); await LastPreviewTask;
            UpdateDraftHint();
            Log("이름·상태 저장 완료 · " + Path.GetFileName(result.Path));
        }
        catch (Exception ex) { Error(ex); }
        finally { _busy = false; FocusRenameInput(); }
    }
    public async Task UndoDraftAsync()
    {
        if (!Writable() || _active == null || !ConfirmDiscard()) return;
        var context = _active.Context;
        if (!_lastLiteEdits.TryGetValue(context.Root, out var edit)) { Log("이번 실행에서 되돌릴 이름·상태·회전 작업이 없습니다."); return; }
        _busy = true;
        try
        {
            var path = await Task.Run(() => LiteWorkspace.Undo(context, edit));
            _lastLiteEdits.Remove(context.Root); _dirty = _statusDirty = false;
            await ReloadAsync(path); await LastPreviewTask; UpdateDraftHint(); Log("마지막 작업을 되돌렸습니다.");
        }
        catch (Exception ex) { Error(ex); }
        finally { _busy = false; }
    }
}
