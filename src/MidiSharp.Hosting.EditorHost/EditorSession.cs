using System;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// A plugin editor embedded in a native X11 window, driven entirely on the <b>caller's</b> thread — it
/// spawns no thread of its own. <see cref="Open"/> creates the window and embeds the editor synchronously;
/// the caller then drives <see cref="PumpOnce"/> in a loop and calls <see cref="Close"/> to tear down.
/// This lets the host run the editor on the <i>same</i> thread that created the plugin, which CLAP requires
/// (its GUI calls deadlock or fail off the creation thread); the audio worker drives it from its command
/// loop, and <see cref="EditorWindow"/> drives it from a dedicated thread for in-process use.
/// </summary>
public sealed class EditorSession : IDisposable
{
    [DllImport("libX11.so.6")] private static extern int XInitThreads();
    static EditorSession() => XInitThreads();

    private readonly IPluginGui _gui;
    private readonly string _title;
    private readonly EditorRunLoop _runLoop = new();
    private static readonly object IdleToken = new();

    private IntPtr _display;
    private ulong _window;
    private IntPtr _wmDelete;
    private IntPtr _ev;
    private int _hostFd;
    private bool _opened;
    private bool _shouldClose;
    private string? _error;

    private EditorSession(IPluginGui gui, string title) { _gui = gui; _title = title; }

    /// <summary>Open the editor on the current thread. Always returns a session; check <see cref="IsOpen"/>
    /// (and <see cref="Error"/>) for success. Returns null only when the plugin has no editor.</summary>
    public static EditorSession? Open(IPluginGui? gui, string title)
    {
        if (gui is not { HasEditor: true }) return null;
        var s = new EditorSession(gui, title);
        s.OpenInternal();
        return s;
    }

    public IEditorRunLoop RunLoop => _runLoop;
    public ulong WindowHandle => _window;
    public IntPtr Display => _display;
    public bool IsOpen => _opened;
    public bool ShouldClose => _shouldClose;
    public string? Error => _error;
    public uint EmbeddedChildCount => _display == IntPtr.Zero ? 0 : X11.ChildCount(_display, _window);

    private void OpenInternal()
    {
        try
        {
            _display = X11.XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) { _error = "XOpenDisplay failed (no display)."; return; }

            var screen = X11.XDefaultScreen(_display);
            var root = X11.XDefaultRootWindow(_display);

            // Bind the run loop, then create the editor — on THIS thread (the plugin's creation thread when
            // the host drives us correctly), so VST3 createView / CLAP gui.create are thread-correct.
            _gui.BindRunLoop(_runLoop);
            if (!_gui.Create("x11", floating: false)) { _error = "plugin gui create(x11) failed."; _gui.BindRunLoop(null); Teardown(); return; }
            _gui.SetScale(1.0);

            var w = 400; var h = 300;
            if (_gui.TryGetSize(out var gw, out var gh) && gw > 0 && gh > 0) { w = gw; h = gh; }

            _window = X11.XCreateSimpleWindow(_display, root, 0, 0, (uint)w, (uint)h, 0,
                X11.XBlackPixel(_display, screen), X11.XWhitePixel(_display, screen));
            X11.XStoreName(_display, _window, _title);
            X11.XSelectInput(_display, _window, X11.StructureNotifyMask | X11.SubstructureNotifyMask);
            _wmDelete = X11.XInternAtom(_display, "WM_DELETE_WINDOW", false);
            X11.XSetWMProtocols(_display, _window, ref _wmDelete, 1);

            if (!_gui.SetParent("x11", _window)) { _error = "plugin gui set_parent failed."; _gui.Destroy(); _gui.BindRunLoop(null); Teardown(); return; }

            // Map our window, then complete the XEMBED handshake: send XEMBED_EMBEDDED_NOTIFY to the plugin's
            // embedded child window — the host's job per the XEMBED spec that CLAP/VST3 X11 embedding follows.
            X11.XMapWindow(_display, _window);
            X11.XSync(_display, false);
            var child = X11.FirstChild(_display, _window);
            if (child != 0) X11.SendXEmbedNotify(_display, _window, child);

            _gui.Show();
            _runLoop.RegisterTimer(30, IdleToken, () => { try { _gui.Idle(); } catch { } });

            _ev = Marshal.AllocHGlobal(256);   // an XEvent union is ~192 bytes
            _hostFd = X11.XConnectionNumber(_display);
            _opened = true;
        }
        catch (Exception ex) { _error = ex.Message; }
    }

    /// <summary>One run-loop iteration: poll the editor's fds (and any the caller registered, e.g. a command
    /// pipe), service window events, plugin fds/timers, and posted work. No-op once closed.</summary>
    public void PumpOnce(int maxWaitMs)
    {
        if (!_opened) return;
        _runLoop.Pump(_hostFd, DrainHostEvents, maxWaitMs);
    }

    private void DrainHostEvents()
    {
        var pending = X11.XPending(_display);
        while (pending-- > 0)
        {
            X11.XNextEvent(_display, _ev);
            if (Marshal.ReadInt32(_ev) == X11.ClientMessage && Marshal.ReadInt64(_ev, 56) == _wmDelete.ToInt64())
                _shouldClose = true;   // window-manager close button
        }
    }

    public void Close()
    {
        if (_display == IntPtr.Zero) return;
        _runLoop.UnregisterTimer(IdleToken);
        try { _gui.Hide(); } catch { }
        try { _gui.Destroy(); } catch { }
        try { _gui.BindRunLoop(null); } catch { }
        Teardown();
    }

    private void Teardown()
    {
        if (_ev != IntPtr.Zero) { Marshal.FreeHGlobal(_ev); _ev = IntPtr.Zero; }
        if (_display != IntPtr.Zero)
        {
            if (_window != 0) { X11.XDestroyWindow(_display, _window); _window = 0; }
            X11.XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
        _opened = false;
    }

    public void Dispose() => Close();
}
