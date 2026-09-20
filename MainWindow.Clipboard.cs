using System.Windows;
using System.Windows.Interop;

namespace BoramRms.Lite;

public partial class MainWindow
{
    private ApplicantNameClipboard? _applicantNameClipboard;
    public Task LastNameCopyTask { get; private set; } = Task.CompletedTask;
    internal Action<string>? NameClipboardWriterForTests { get; set; }

    private ApplicantNameClipboard NameClipboard => _applicantNameClipboard ??= new(name =>
    {
        // All existing UI tests run without touching the operator's clipboard.
        if (TestMode) { NameClipboardWriterForTests?.Invoke(name); return; }
        // WPF's usual synchronous retry loop can stall navigation. Retry above,
        // asynchronously, and only while the same application remains selected.
        UnicodeClipboardWriter.Write(new WindowInteropHelper(this).Handle, name);
    });

    private void QueueApplicantNameCopy(ImageItem item, bool force = false)
    {
        var path = item.FullPath; var name = item.Name.Trim();
        LastNameCopyTask = CopyApplicantNameAsync(path, name, force);
    }

    private async Task CopyApplicantNameAsync(string path, string name, bool force)
    {
        bool Current() => !_closing && _editing != null && SafePaths.Same(_editing.FullPath, path) && _editing.Name.Trim() == name;
        try
        {
            var result = await NameClipboard.RequestAsync(path, name, Current, force);
            if (!Current()) return;
            if (result == ApplicantNameCopyResult.Unavailable)
                Log("이름 자동 복사 대기 · 다른 프로그램이 클립보드를 사용 중입니다. '이름 복사'로 다시 복사하세요.");
            else if (force && result == ApplicantNameCopyResult.Copied) Log("신청서 이름만 복사했습니다.");
            else if (force && result == ApplicantNameCopyResult.NoName) Log("복사할 신청서 이름이 없습니다.");
        }
        catch (Exception)
        {
            // Clipboard integration must not cancel navigation or status saves.
            if (Current()) Log("이름을 복사하지 못했습니다. '이름 복사'로 다시 시도하세요.");
        }
    }

    private void ResetApplicantNameCopy(bool forgetSelection = false) => _applicantNameClipboard?.CancelPending(forgetSelection);

    private async void CopyApplicantName_Click(object sender, RoutedEventArgs e)
    {
        if (_editing == null || _closing) { Log("신청서를 먼저 선택하세요."); return; }
        // Use the saved applicant name, never quota, draft text, phone or status.
        QueueApplicantNameCopy(_editing, force: true);
        await LastNameCopyTask;
    }
}
