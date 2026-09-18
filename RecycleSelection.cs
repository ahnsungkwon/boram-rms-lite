using System.IO;
using System.Runtime.InteropServices;
namespace BoramRms.Lite;

public sealed record RecycleEntry(string Path, string Hash, long Length, long ModifiedTicks);
public sealed class RecycleResult
{
    public int Done { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = new();
}
public static class RecycleSelection
{
    public static IReadOnlyList<RecycleEntry> Prepare(FolderContext context, IEnumerable<ImageItem> selected)
    {
        var result = new List<RecycleEntry>();
        foreach (var item in selected.DistinctBy(i => SafePaths.Normalize(i.FullPath), StringComparer.OrdinalIgnoreCase))
        {
            ValidatePath(context, item.FullPath);
            LocalData.EnsureUnchanged(item);
            result.Add(new(item.FullPath, SafePaths.Hash(item.FullPath), item.Length, item.ModifiedTicks));
        }
        return result.AsReadOnly();
    }
    private static void ValidatePath(FolderContext context, string path)
    {
        if (!SafePaths.Under(path, context.Root) || SafePaths.Same(path, context.Root) || !LocalData.Extensions.Contains(Path.GetExtension(path)))
            throw new IOException("현재 작업 폴더 안의 선택된 이미지 파일만 처리할 수 있습니다.");
        SafePaths.NoLinks(path);
        if (Path.GetRelativePath(context.Root, path).Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.')))
            throw new IOException("앱 기록/숨김 파일은 휴지통 처리 대상이 아닙니다.");
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Directory)) != 0)
            throw new IOException("읽기 전용 파일이나 폴더는 처리하지 않습니다.");
    }
    public static RecycleResult Execute(FolderContext context, IReadOnlyList<RecycleEntry> plan, Action<string> sendToRecycleBin)
    {
        var result = new RecycleResult();
        foreach (var item in plan)
        {
            try
            {
                ValidatePath(context, item.Path);
                var file = new FileInfo(item.Path);
                if (file.Length != item.Length || file.LastWriteTimeUtc.Ticks != item.ModifiedTicks || SafePaths.Hash(item.Path) != item.Hash)
                    throw new IOException("선택 이후 파일이 바뀌어 이동하지 않았습니다.");
                sendToRecycleBin(item.Path);
                if (File.Exists(item.Path)) throw new IOException("휴지통 이동이 완료되지 않았습니다.");
                result.Done++;
            }
            catch (OperationCanceledException) { result.Cancelled = true; break; }
            catch (Exception ex) { result.Errors.Add(Path.GetFileName(item.Path) + " · " + ex.Message); }
        }
        return result;
    }
    public static Task<RecycleResult> RunAsync(FolderContext context, IReadOnlyList<RecycleEntry> plan, IntPtr owner)
    {
        var completion = new TaskCompletionSource<RecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Execute(context, plan, path => NativeRecycle.Send(path, owner))); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "RMS Lite recycle" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}

internal static class NativeRecycle
{
    // IFileOperation is STA-only. Request recycling explicitly; never fall back to File.Delete.
    // Microsoft: IFileOperation::SetOperationFlags / FOFX_RECYCLEONDELETE.
    internal const uint Flags = 0x00080000 | 0x20000000 | 0x00100000 | 0x0400 | 0x0004 | 0x0010 | 0x2000 | 0x4000;
    public static void Send(string path, IntPtr owner)
    {
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("이 경로의 Windows 휴지통 지원을 확인할 수 없습니다. 영구삭제로 전환하지 않습니다.");
        IFileOperation? operation = null; IntPtr shellItem = IntPtr.Zero;
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            operation.SetOperationFlags(Flags); operation.SetOwnerWindow(owner);
            var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out shellItem));
            operation.DeleteItem(shellItem, IntPtr.Zero); operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted) throw new OperationCanceledException("Windows 휴지통 이동이 취소되었습니다.");
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x800704C7)) { throw new OperationCanceledException("휴지통 이동 취소", ex); }
        finally
        {
            if (shellItem != IntPtr.Zero) Marshal.Release(shellItem);
            if (operation != null && Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
        }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IntPtr item);
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr changes);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems(IntPtr items, IntPtr folder);
        void CopyItem(IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr folder);
        void DeleteItem(IntPtr item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
