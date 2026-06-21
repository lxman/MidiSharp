using System;
using System.Threading;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.EditorHost.Win32;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>The Windows (Win32) windowing backend.</summary>
internal sealed class Win32Platform : IEditorPlatform
{
    private static int _dpiTried;
    private static bool? _available;

    public bool IsAvailable => _available ??= Detect();

    public INativeEditorWindow? CreateWindow(string title, int width, int height)
    {
        EnsureDpiAware();
        var w = new Win32EditorWindow();
        return w.Open(title, width, height) ? w : null;
    }

    // Set per-process DPI awareness once; harmless if the host already set it (returns false → ignored).
    private static void EnsureDpiAware()
    {
        if (Interlocked.Exchange(ref _dpiTried, 1) != 0) return;
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
    }

    // True when a window can actually be created (an interactive desktop). Creating a hidden top-level
    // "STATIC" window needs no class registration and fails on a non-interactive station (a service).
    private static bool Detect()
    {
        try
        {
            var hwnd = CreateWindowExW(0, "STATIC", null, 0, IntPtr.Zero, IntPtr.Zero, 0, 0,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return false;
            DestroyWindow(hwnd);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>
/// A top-level Win32 window that hosts an embedded plugin editor: it owns the window, a per-instance window
/// class with a black background, and a message-pump <see cref="Win32RunLoop"/> that services the window's
/// messages alongside the plugin's timers. The Windows analogue of <see cref="X11EditorWindow"/>; all Win32
/// detail is confined to this class (and <see cref="Win32"/>), so the rest of the editor host is
/// windowing-agnostic. Created and pumped on a single UI thread.
/// </summary>
internal sealed class Win32EditorWindow : INativeEditorWindow
{
    private static int _seq;

    private readonly Win32RunLoop _runLoop = new();
    private readonly WndProc _wndProc;          // kept alive for the window's lifetime (GC root)
    private readonly string _className;
    private IntPtr _hwnd;
    private IntPtr _hInstance;
    private bool _shouldClose;

    public Win32EditorWindow()
    {
        _wndProc = WindowProc;
        _className = $"MidiSharpEdHost_{Environment.ProcessId}_{Interlocked.Increment(ref _seq)}";
    }

    public string WindowApi => "win32";
    public ulong Handle => (ulong)_hwnd;
    public IEditorRunLoop RunLoop => _runLoop;
    public bool ShouldClose => _shouldClose;

    public uint EmbeddedChildCount
    {
        get
        {
            if (_hwnd == IntPtr.Zero) return 0;
            uint n = 0;
            for (var c = GetWindow(_hwnd, GW_CHILD); c != IntPtr.Zero; c = GetWindow(c, GW_HWNDNEXT)) n++;
            return n;
        }
    }

    internal bool Open(string title, int width, int height)
    {
        _hInstance = GetModuleHandleW(null);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            // Background BLACK (not white): the embedded plugin owns the pixels, but a white parent shows
            // through compositing seams as bright lines behind a dark editor (same fix as the X11 backend).
            hbrBackground = GetStockObject(BLACK_BRUSH),
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref wc) == 0) { _hInstance = IntPtr.Zero; return false; }

        var (winW, winH) = WindowSizeForClient(width, height);
        _hwnd = CreateWindowExW(0, _className, title, WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, winW, winH, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) { UnregisterClassW(_className, _hInstance); _hInstance = IntPtr.Zero; return false; }
        return true;
    }

    public void Resize(int width, int height)
    {
        if (_hwnd == IntPtr.Zero || width <= 0 || height <= 0) return;
        var (winW, winH) = WindowSizeForClient(width, height);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, winW, winH, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        var child = GetWindow(_hwnd, GW_CHILD);
        if (child != IntPtr.Zero)
            SetWindowPos(child, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void Map() => ShowWindow(_hwnd, SW_SHOW);

    public void CompleteEmbed() { }   // no XEMBED-style handshake on Win32

    public void PumpOnce(int maxWaitMs) => _runLoop.Pump(maxWaitMs);

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_CLOSE) { _shouldClose = true; return IntPtr.Zero; }   // host controls teardown
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // Outer window size that yields a width×height client area for an overlapped window.
    private static (int w, int h) WindowSizeForClient(int width, int height)
    {
        var r = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        AdjustWindowRectEx(ref r, WS_OVERLAPPEDWINDOW, false, 0);
        return (r.Right - r.Left, r.Bottom - r.Top);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_hInstance != IntPtr.Zero) { UnregisterClassW(_className, _hInstance); _hInstance = IntPtr.Zero; }
    }
}
