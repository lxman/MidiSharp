using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>The X11 (Linux/BSD, and macOS via XQuartz) windowing backend.</summary>
internal sealed class X11Platform : IEditorPlatform
{
    public bool IsAvailable => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    public INativeEditorWindow? CreateWindow(string title, int width, int height)
    {
        var w = new X11EditorWindow();
        return w.Open(title, width, height) ? w : null;
    }
}

/// <summary>
/// A top-level X11 window that hosts an embedded plugin editor: it owns the display connection, the window,
/// and a poll-based <see cref="EditorRunLoop"/> that services the window's events alongside the plugin's
/// registered fds/timers. The XEMBED handshake and the host/child resize live here too. All X11/Xlib detail
/// is confined to this class (and <see cref="X11"/>); the rest of the editor host is windowing-agnostic.
/// </summary>
internal sealed class X11EditorWindow : INativeEditorWindow
{
    [DllImport("libX11.so.6")] private static extern int XInitThreads();
    static X11EditorWindow() => XInitThreads();

    private readonly EditorRunLoop _runLoop = new();
    private IntPtr _display;
    private ulong _window;
    private IntPtr _wmDelete;
    private IntPtr _ev;
    private int _hostFd;
    private bool _shouldClose;

    public string WindowApi => "x11";
    public ulong Handle => _window;
    public IEditorRunLoop RunLoop => _runLoop;
    public bool ShouldClose => _shouldClose;
    public uint EmbeddedChildCount => _display == IntPtr.Zero ? 0 : X11.ChildCount(_display, _window);

    internal bool Open(string title, int width, int height)
    {
        _display = X11.XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero) return false;

        int screen = X11.XDefaultScreen(_display);
        ulong root = X11.XDefaultRootWindow(_display);
        // Background BLACK (not white): the embedded plugin owns the pixels, but a white parent background
        // shows through compositing seams as bright lines behind a dark editor.
        _window = X11.XCreateSimpleWindow(_display, root, 0, 0, (uint)width, (uint)height, 0,
            X11.XBlackPixel(_display, screen), X11.XBlackPixel(_display, screen));
        X11.XStoreName(_display, _window, title);
        X11.XSelectInput(_display, _window, X11.StructureNotifyMask | X11.SubstructureNotifyMask);
        _wmDelete = X11.XInternAtom(_display, "WM_DELETE_WINDOW", false);
        X11.XSetWMProtocols(_display, _window, ref _wmDelete, 1);

        _ev = Marshal.AllocHGlobal(256);   // an XEvent union is ~192 bytes
        _hostFd = X11.XConnectionNumber(_display);
        return true;
    }

    public void Resize(int width, int height)
    {
        if (_display == IntPtr.Zero || width <= 0 || height <= 0) return;
        X11.XResizeWindow(_display, _window, (uint)width, (uint)height);
        ulong child = X11.FirstChild(_display, _window);
        if (child != 0) X11.XResizeWindow(_display, child, (uint)width, (uint)height);
        X11.XFlush(_display);
    }

    public void Map()
    {
        X11.XMapWindow(_display, _window);
        X11.XSync(_display, false);
    }

    public void CompleteEmbed()
    {
        // After the plugin parented its window, notify it per the XEMBED spec — some editors' show() blocks
        // until they receive XEMBED_EMBEDDED_NOTIFY.
        ulong child = X11.FirstChild(_display, _window);
        if (child != 0) X11.SendXEmbedNotify(_display, _window, child);
    }

    public void PumpOnce(int maxWaitMs) => _runLoop.Pump(_hostFd, DrainEvents, maxWaitMs);

    private void DrainEvents()
    {
        int pending = X11.XPending(_display);
        while (pending-- > 0)
        {
            X11.XNextEvent(_display, _ev);
            if (Marshal.ReadInt32(_ev) == X11.ClientMessage && Marshal.ReadInt64(_ev, 56) == _wmDelete.ToInt64())
                _shouldClose = true;   // window-manager close button
        }
    }

    public void Dispose()
    {
        if (_ev != IntPtr.Zero) { Marshal.FreeHGlobal(_ev); _ev = IntPtr.Zero; }
        if (_display != IntPtr.Zero)
        {
            if (_window != 0) { X11.XDestroyWindow(_display, _window); _window = 0; }
            X11.XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
    }
}
