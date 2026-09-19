using System.IO;
using System.Security.Cryptography;
namespace BoramRms.Lite;

// Retry a single reversible I/O step, never the entire save/rename/rotation.
internal static class LiteFileIo
{
    private static readonly int[] RetryDelaysMs = { 40, 80, 120, 200, 300, 400, 600 };
    internal static readonly AsyncLocal<Action<string>?> TempPreparedForTests = new();
    internal static readonly AsyncLocal<Action<int, IOException>?> RetryingForTests = new();
    internal static bool IsSharingViolation(IOException error) => (error.HResult & 0xffff) is 32 or 33;

    internal static T Retry<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (IOException error) when (IsSharingViolation(error) && attempt < RetryDelaysMs.Length)
            {
                RetryingForTests.Value?.Invoke(attempt + 1, error);
                // Callers perform disk writes on worker threads; the UI stays responsive.
                Thread.Sleep(RetryDelaysMs[attempt]);
            }
        }
    }
    internal static void Retry(Action action) => Retry(() => { action(); return true; });
    internal static string Describe(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is IOException io && IsSharingViolation(io))
                return "파일 사용이 끝나기를 기다렸지만 잠금이 계속됩니다. 잠시 후 같은 작업을 다시 시도하세요. (Windows 파일 잠금 " + (io.HResult & 0xffff) + ")";
        return error.Message;
    }
    private static FileStream OpenSnapshot(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    internal static byte[] ReadBytes(string path) => Retry(() =>
    {
        using var source = OpenSnapshot(path);
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        return memory.ToArray();
    });
    internal static string? Revision(string path) => Retry(() =>
    {
        try { using var source = OpenSnapshot(path); return Convert.ToHexString(SHA256.HashData(source)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    });

    internal static void AtomicReplace(string path, byte[] bytes, string? expected, string? backup)
    {
        SafePaths.NoLinks(path);
        if (backup != null) SafePaths.NoLinks(backup);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".lite-" + Guid.NewGuid().ToString("N") + ".tmp");
        var ownsTemp = false;
        try
        {
            using (var stream = Retry(() => new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                ownsTemp = true;
                stream.Write(bytes);
                stream.Flush(true);
            }
            // No callback is installed during ordinary use. Tests acquire real Windows
            // sharing locks here after the writer has closed, before the rename step.
            TempPreparedForTests.Value?.Invoke(temp);
            Retry(() =>
            {
                SafePaths.NoLinks(path); SafePaths.NoLinks(temp);
                if (backup != null) SafePaths.NoLinks(backup);
                if (expected == null)
                {
                    if (File.Exists(path) || Directory.Exists(path)) throw new IOException("저장 중 대상 파일이 생겼습니다. 기존 파일은 덮어쓰지 않았습니다.");
                    File.Move(temp, path); // Never overwrite a concurrently created file.
                }
                else
                {
                    // Keep the validated snapshot open without blocking replacement.
                    // Check on EVERY attempt so a wait cannot overwrite a newer edit.
                    using var original = OpenSnapshot(path);
                    if (Convert.ToHexString(SHA256.HashData(original)) != expected)
                        throw new IOException("저장 중 다른 프로그램에서 파일이 바뀌었습니다. 기존 파일은 덮어쓰지 않았습니다.");
                    if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                        throw new UnauthorizedAccessException("저장 대상이 읽기 전용입니다. 기존 파일은 바꾸지 않았습니다.");
                    File.Replace(temp, path, backup);
                }
            });
        }
        finally
        {
            // Only our unique staging file is eligible. A scanner may still hold it;
            // cleanup must NEVER turn a committed save into a failure or hide the cause.
            // Leftover .tmp files are not state records and never block later work.
            if (ownsTemp)
            {
                try { if (File.Exists(temp)) { SafePaths.NoLinks(temp); File.Delete(temp); } }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
