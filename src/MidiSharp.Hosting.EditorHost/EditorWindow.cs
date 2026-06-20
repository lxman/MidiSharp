using System;
using System.Runtime.InteropServices;
using System.Threading;
using MidiSharp.Hosting;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// Hosts a plugin's editor in a native top-level X11 window. <see cref="Open"/> creates a window sized to
/// the editor's preferred size, embeds the editor (CLAP <c>create("x11", embedded)</c> → <c>set_parent</c>
/// → <c>show</c>), maps it, and runs the window's event loop on a dedicated thread. Because a plugin's
/// editor calls are main-thread-only, the whole window + editor lifecycle runs on that one thread; the
/// audio thread keeps calling <c>Process</c> independently (which CLAP permits).
/// </summary>
/// <remarks>
/// The host pumps its own top-level window's events (close, resize). A plugin draws into its embedded child
/// window through its own connection/timers; a plugin that needs host timer callbacks to redraw isn't
/// served yet (the fixture paints via an X server background, so it needs none). Linux/X11 only.
/// </remarks>
public sealed class EditorWindow : IDisposable
{
    [DllImport("libX11.so.6")] private static extern int XInitThreads();

    private readonly IPluginGui _gui;
    private readonly string _title;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    private IntPtr _display;
    private ulong _window;
    private IntPtr _wmDelete;
    private volatile bool _running;
    private bool _opened;
    private string? _error;
    private readonly EditorRunLoop _runLoop = new();
    private static readonly object IdleToken = new();

    static EditorWindow() => XInitThreads();

    private EditorWindow(IPluginGui gui, string title)
    {
        _gui = gui;
        _title = title;
        _thread = new Thread(Run) { IsBackground = true, Name = "plugin-editor" };
    }

    /// <summary>Open the editor window for a plugin's GUI. Returns null (with no window) when the plugin has
    /// no editor, can't present over X11, or the embed fails. Blocks until the window is up or has failed.</summary>
    public static EditorWindow? Open(IPluginGui? gui, string title)
    {
        // Only the cheap capability check here (no view creation): everything that touches the editor — for
        // VST3 that includes createView — must run on the editor thread, so it's deferred into Run().
        if (gui is not { HasEditor: true }) return null;
        var w = new EditorWindow(gui, title);
        w._thread.Start();
        w._ready.Wait();
        if (!w._opened) { w.Close(); return null; }
        return w;
    }

    /// <summary>The host (top-level) window's X11 id — the editor is embedded as its child.</summary>
    public ulong WindowHandle => _window;
    public IntPtr Display => _display;
    public bool IsOpen => _opened && _running;
    public string? Error => _error;

    /// <summary>Direct child windows of the host window — ≥1 once the plugin has embedded its editor.</summary>
    public uint EmbeddedChildCount => _display == IntPtr.Zero ? 0 : X11.ChildCount(_display, _window);

    private void Run()
    {
        try
        {
            _display = X11.XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) { _error = "XOpenDisplay failed (no display)."; _ready.Set(); return; }

            var screen = X11.XDefaultScreen(_display);
            var root = X11.XDefaultRootWindow(_display);

            // Give the plugin our run loop, then create the editor — for VST3 createView happens here, on
            // this UI thread, so the whole view lifecycle (create → attach → pump → destroy) is single-threaded.
            _gui.BindRunLoop(_runLoop);
            if (!_gui.Create("x11", floating: false)) { _error = "plugin gui create(x11) failed."; _gui.BindRunLoop(null); Teardown(); _ready.Set(); return; }
            _gui.SetScale(1.0);

            var w = 400; var h = 300;
            if (_gui.TryGetSize(out var gw, out var gh) && gw > 0 && gh > 0) { w = gw; h = gh; }

            _window = X11.XCreateSimpleWindow(_display, root, 0, 0, (uint)w, (uint)h, 0,
                X11.XBlackPixel(_display, screen), X11.XWhitePixel(_display, screen));
            X11.XStoreName(_display, _window, _title);
            X11.XSelectInput(_display, _window, X11.StructureNotifyMask | X11.SubstructureNotifyMask);
            _wmDelete = X11.XInternAtom(_display, "WM_DELETE_WINDOW", false);
            X11.XSetWMProtocols(_display, _window, ref _wmDelete, 1);

            // Embed: parent the editor into our window → show. During attach the plugin registers its own
            // fds/timers with our run loop (VST3 IRunLoop / CLAP timer+fd ext).
            if (!_gui.SetParent("x11", _window)) { _error = "plugin gui set_parent failed."; _gui.Destroy(); _gui.BindRunLoop(null); Teardown(); _ready.Set(); return; }
            _gui.Show();
            // A steady idle tick for formats that repaint via a host idle call (VST2 effEditIdle); harmless otherwise.
            _runLoop.RegisterTimer(30, IdleToken, () => { try { _gui.Idle(); } catch { } });

            X11.XMapWindow(_display, _window);
            X11.XSync(_display, false);

            _opened = true;
            _running = true;
            _ready.Set();
            Pump();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _ready.Set();
        }
    }

    private void Pump()
    {
        var ev = Marshal.AllocHGlobal(256);   // an XEvent union is ~192 bytes
        var hostFd = X11.XConnectionNumber(_display);
        try
        {
            while (_running)
                _runLoop.Pump(hostFd, () => DrainHostEvents(ev), maxWaitMs: 30);
        }
        finally
        {
            Marshal.FreeHGlobal(ev);
            _runLoop.UnregisterTimer(IdleToken);
            try { _gui.Hide(); } catch { }
            try { _gui.Destroy(); } catch { }
            try { _gui.BindRunLoop(null); } catch { }
            Teardown();
        }
    }

    private void DrainHostEvents(IntPtr ev)
    {
        var pending = X11.XPending(_display);
        while (pending-- > 0 && _running)
        {
            X11.XNextEvent(_display, ev);
            if (Marshal.ReadInt32(ev) == X11.ClientMessage && Marshal.ReadInt64(ev, 56) == _wmDelete.ToInt64())
                _running = false;   // window-manager close button
        }
    }

    private void Teardown()
    {
        if (_display != IntPtr.Zero)
        {
            if (_window != 0) { X11.XDestroyWindow(_display, _window); _window = 0; }
            X11.XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
        _opened = false;
    }

    /// <summary>Close the editor and its window; blocks until the editor thread has torn everything down.</summary>
    public void Close()
    {
        _running = false;
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(2000);
        if (Thread.CurrentThread != _thread) Teardown();   // thread never started (e.g. open failed early)
    }

    public void Dispose() => Close();
}
