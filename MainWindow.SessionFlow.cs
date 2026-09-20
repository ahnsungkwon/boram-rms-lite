using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private async Task SelectTabWithSaveAsync(WorkTab? requested)
    {
        if (requested == null || requested == _active || _navigating || _busy || _loading) return;
        _navigating = true;
        try
        {
            if (!await TrySaveCurrentAsync()) return;
            _selecting = true; WorkTabs.SelectedItem = requested; _selecting = false;
            _active = requested;
            await ReloadAsync(requested.SelectedPath); await LastPreviewTask; UpdateDraftHint(); FocusRenameInput();
        }
        finally { _navigating = false; }
    }
    private async Task ApplyFilterWithSaveAsync()
    {
        if (_navigating || _busy || _loading) return;
        _navigating = true;
        try { if (await TrySaveCurrentAsync()) { Rebind(); await LastPreviewTask; } }
        finally { _navigating = false; }
    }
    public async Task RestorePreviousFoldersAsync()
    {
        if (_busy || _navigating || !await TrySaveCurrentAsync()) return;
        if (_previousSettings.Folders.Count == 0) { Log("지난번에 열었던 폴더가 없습니다. '+ 폴더 선택'으로 작업을 시작하세요."); return; }
        int opened = 0, missing = 0;
        foreach (var path in _previousSettings.Folders)
        {
            if (!Directory.Exists(path)) { missing++; continue; }
            await OpenFolderAsync(path); await LastPreviewTask;
            if (_active != null && _previousSettings.Selected.TryGetValue(path, out var selected)) { Rebind(selected); await LastPreviewTask; }
            opened++;
        }
        Log($"이전 폴더 {opened}개 다시 열기 완료" + (missing > 0 ? $" · 찾을 수 없는 폴더 {missing}개" : "") + " · 파일 내용은 이전 상태로 되돌리지 않습니다.");
    }
    public const string WorkflowHelpText = "색상 테마\n상단 오른쪽 테마 버튼에서 녹색·블루·보라·핑크를 선택합니다. 즉시 바뀌며 다음 실행에도 유지됩니다. 신청서 이미지와 체크 기록, 입력 중 이름은 변경하지 않습니다. 오류·보완·취소 및 구좌별 표시색은 의미를 유지합니다.\n\n체크는 바로 저장됩니다\n보완 사유·카드·주말미등록·추가·입력전취소·입력후취소·미성년자는 체크와 해제가 바로 저장됩니다. 여러 보완 사유와 카드·추가·미성년자 등을 함께 선택할 수 있습니다. 입력전취소와 입력후취소만 서로 대체됩니다.\n\n이름 저장과 이동\nSpace: 이름 저장 후 다음 이미지. Ctrl+Space: 이름 저장 후 이전 이미지. 이름이 비어 있으면 기존 파일명을 유지하고 이동합니다. 구좌수나 이름을 새로 넣지 않아도 됩니다. 목록이나 좌우 화살표로 이동할 때도 현재 입력을 저장합니다. 저장에 실패하면 이동하지 않고 파일명과 이유를 하단에 표시합니다. 검색·메모 입력칸의 공백은 그대로 입력됩니다.\n\n이전 폴더 열기 (기존 작업 복원)\n지난번 앱 종료 때 열려 있던 폴더와 선택한 이미지를 다시 엽니다. 삭제된 파일을 복구하거나 이름·체크를 과거 상태로 바꾸는 기능이 아닙니다.\n\n마지막 변경 취소 (기존 되돌리기)\n현재 폴더에서 이번 실행 중 마지막으로 저장한 이름·체크·메모 또는 회전 1건을 취소합니다. 버튼에 마우스를 올리면 대상 파일과 작업이 표시됩니다. 파일 삭제·300KB 압축·업데이트는 취소할 수 없으며 앱을 종료하면 취소 이력은 초기화됩니다.\n\n입력 원래대로\n이름 입력 아래에 항상 보이는 버튼입니다. 아직 저장되지 않은 이름·메모·실패한 체크 입력만 저장된 값으로 다시 읽습니다. 빈 이름으로 이동할 때 이 버튼을 누를 필요는 없습니다. 이미 자동 저장된 체크는 취소하지 않습니다.\n\n안전 확인\n선택 파일 휴지통 이동과 원본 압축의 확인창은 유지합니다. 체크를 저장해도 이미지 파일을 다른 폴더로 옮기지 않습니다.";
    private void WorkflowHelp_Click(object sender, RoutedEventArgs e)
    {
        var existing = OwnedWindows.OfType<GuideWindow>().FirstOrDefault();
        if (existing != null) { if (!TestMode) existing.Activate(); return; }
        var guide = new GuideWindow { Owner = this };
        if (TestMode)
        {
            guide.WindowStartupLocation = WindowStartupLocation.Manual;
            guide.Left = -16000; guide.Top = -16000;
            guide.ShowInTaskbar = false; guide.ShowActivated = false;
        }
        guide.Show();
    }
}
