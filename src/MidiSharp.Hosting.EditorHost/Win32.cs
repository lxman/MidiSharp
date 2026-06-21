using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// The slice of Win32 (user32/gdi32) needed to host a plugin editor: register a window class, create a
/// top-level window to embed the editor into, show/resize it, pump its message queue (so resize/close work),
/// and tear it down. This is the Windows analogue of <see cref="X11"/>; all Win32 P/Invoke lives here and in
/// <see cref="Win32EditorWindow"/>. All of it is main-thread work, off the audio path.
/// </summary>
internal static partial class Win32
{
    private const string User32 = "user32.dll";

    // ── Message pump ──
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    public const uint PM_REMOVE = 0x0001;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint MWMO_INPUTAVAILABLE = 0x0004;

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG msg);

    [LibraryImport(User32)]
    public static partial IntPtr DispatchMessageW(in MSG msg);

    // nCount=0 + pHandles=NULL is valid: wait for input or timeout. Returns when a message is queued or the
    // timeout elapses, whichever comes first.
    [LibraryImport(User32)]
    public static partial uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr pHandles, uint milliseconds, uint wakeMask, uint flags);
}
