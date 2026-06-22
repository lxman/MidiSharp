using System;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// A plugin editor embedded in a native host window, driven entirely on the <b>caller's</b> thread (it spawns
/// no thread of its own). <see cref="Open"/> creates the window and embeds the editor synchronously; the
/// caller then drives <see cref="PumpOnce"/> in a loop and calls <see cref="Close"/> to tear down. Running it
/// on the same thread that created the plugin is what CLAP requires (its GUI calls deadlock off the creation
/// thread); the audio worker drives it from its command loop, and <see cref="EditorWindow"/> drives it from a
/// dedicated thread for in-process use.
/// </summary>
/// <remarks>
/// This class is windowing-system-agnostic: it sequences the format-agnostic embed steps
/// (<see cref="IPluginGui"/>) against an <see cref="INativeEditorWindow"/> from <see cref="EditorPlatform"/>.
/// All X11/Win32/Cocoa detail lives in the platform backend.
/// </remarks>
public sealed class EditorSession : IDisposable
{
    private readonly IPluginGui _gui;
    private static readonly object IdleToken = new();

    private INativeEditorWindow? _window;
    private bool _opened;
    private string? _error;

    private EditorSession(IPluginGui gui) => _gui = gui;

    /// <summary>Open the editor on the current thread. Always returns a session; check <see cref="IsOpen"/>
    /// (and <see cref="Error"/>) for success. Returns null only when the plugin has no editor.</summary>
    public static EditorSession? Open(IPluginGui? gui, string title)
    {
        if (gui is not { HasEditor: true }) return null;
        var s = new EditorSession(gui);
        s.OpenInternal(title);
        return s;
    }

    public IEditorRunLoop? RunLoop => _window?.RunLoop;
    public ulong WindowHandle => _window?.Handle ?? 0;
    public bool IsOpen => _opened;
    public bool ShouldClose => _window?.ShouldClose ?? true;
    public string? Error => _error;
    public uint EmbeddedChildCount => _window?.EmbeddedChildCount ?? 0;

    private void OpenInternal(string title)
    {
        try
        {
            IEditorPlatform platform = EditorPlatform.Current;
            if (!platform.IsAvailable) { _error = "no windowing backend available (no display?)."; return; }

            // A default starting size; the real size is queried from the plugin after gui.Create() below (CLAP
            // forbids gui.size() before create()) and again after show() once it has laid out. The window is
            // resized before it's mapped, so this provisional size is never shown.
            _window = platform.CreateWindow(title, 400, 300);
            if (_window == null) { _error = "could not create a host window."; return; }

            // Bind the run loop, then create the editor — on THIS thread, so VST3 createView / CLAP gui.create
            // are thread-correct. The plugin registers its fds/timers with the window's run loop here.
            _gui.BindRunLoop(_window.RunLoop);
            if (!_gui.Create(_window.WindowApi, floating: false)) { _error = $"plugin gui create({_window.WindowApi}) failed."; _gui.BindRunLoop(null); Teardown(); return; }
            _gui.SetScale(1.0);
            if (_gui.TryGetSize(out int gw, out int gh) && gw > 0 && gh > 0) _window.Resize(gw, gh);

            if (!_gui.SetParent(_window.WindowApi, _window.Handle)) { _error = "plugin gui set_parent failed."; _gui.Destroy(); _gui.BindRunLoop(null); Teardown(); return; }
            _window.Map();
            _window.CompleteEmbed();
            _gui.Show();

            // After show() the plugin has laid out and may report its real size — apply it.
            if (_gui.TryGetSize(out int rw, out int rh) && rw > 0 && rh > 0) _window.Resize(rw, rh);

            _window.RunLoop.RegisterTimer(30, IdleToken, () => { try { _gui.Idle(); } catch { } });
            _opened = true;
        }
        catch (Exception ex) { _error = ex.Message; }
    }

    /// <summary>One event-loop iteration on the window's run loop (and any fds the caller registered on it).</summary>
    public void PumpOnce(int maxWaitMs)
    {
        if (_opened) _window!.PumpOnce(maxWaitMs);
    }

    public void Close()
    {
        if (_window == null) return;
        _window.RunLoop.UnregisterTimer(IdleToken);
        try { _gui.Hide(); } catch { }
        try { _gui.Destroy(); } catch { }
        try { _gui.BindRunLoop(null); } catch { }
        Teardown();
    }

    private void Teardown()
    {
        _window?.Dispose();
        _window = null;
        _opened = false;
    }

    public void Dispose() => Close();
}
