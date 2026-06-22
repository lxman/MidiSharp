using System;
using System.Runtime.InteropServices;
// ReSharper disable InconsistentNaming

namespace MidiSharp.Hosting.EditorHost.Windows;

/// <summary>
/// The slice of Win32 (user32/gdi32) needed to host a plugin editor: register a window class, create a
/// top-level window to embed the editor into, show/resize it, pump its message queue (so resize/close work),
/// and tear it down. This is the Windows analogue of <see cref="Linux.X11"/>; all Win32 P/Invoke lives here and in
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

    // ── Window class + creation ──
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const int SW_SHOW = 5;
    public const uint WM_CLOSE = 0x0010;
    public const int GW_CHILD = 5;
    public const int GW_HWNDNEXT = 2;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const int BLACK_BRUSH = 4;   // GetStockObject index
    public static readonly IntPtr CW_USEDEFAULT = unchecked((IntPtr)(int)0x80000000);
    // PerMonitorV2 DPI awareness context (Win10 1703+).
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterClassW(string className, IntPtr hInstance);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        IntPtr x, IntPtr y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport(User32)]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32)]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustWindowRectEx(ref RECT rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("gdi32.dll")]
    public static extern IntPtr GetStockObject(int index);
}
