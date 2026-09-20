using System.Runtime.InteropServices;

namespace BoramRms.Lite;

internal static class UnicodeClipboardWriter
{
    internal static void Write(IntPtr owner, string name)
    {
        if (owner == IntPtr.Zero) throw new InvalidOperationException("신청서 창이 아직 준비되지 않았습니다.");
        var characters = (name + '\0').ToCharArray();
        var memory = GlobalAlloc(0x0002, checked((nuint)characters.Length * 2)); // GMEM_MOVEABLE
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        bool opened = false;
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
            try { Marshal.Copy(characters, 0, pointer, characters.Length); }
            finally { GlobalUnlock(memory); }
            // Single attempt; the caller schedules bounded non-blocking retries.
            if (!OpenClipboard(owner)) throw ClipboardError();
            opened = true;
            if (!EmptyClipboard()) throw ClipboardError();
            if (SetClipboardData(13, memory) == IntPtr.Zero) throw ClipboardError(); // CF_UNICODETEXT
            memory = IntPtr.Zero; // Windows owns the allocation after a successful write.
        }
        finally
        {
            if (opened) CloseClipboard();
            if (memory != IntPtr.Zero) GlobalFree(memory);
        }
    }

    private static ExternalException ClipboardError() => new("클립보드를 사용할 수 없습니다.", Marshal.GetHRForLastWin32Error());
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
