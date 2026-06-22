using System;
using System.Threading;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// Runs an <see cref="EditorSession"/> on its own dedicated thread — the in-process convenience wrapper
/// (used by tests and any host that doesn't already have a plugin-creation thread to drive the editor on).
/// A host that must run the editor on a specific thread (the out-of-process worker, which has to use the
/// plugin's creation thread for CLAP) drives an <see cref="EditorSession"/> directly instead.
/// </summary>
public sealed class EditorWindow : IDisposable
{
    private readonly IPluginGui _gui;
    private readonly string _title;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private EditorSession? _session;
    private volatile bool _running;
    private string? _error;

    private EditorWindow(IPluginGui gui, string title)
    {
        _gui = gui;
        _title = title;
        _thread = new Thread(Run) { IsBackground = true, Name = "plugin-editor" };
    }

    /// <summary>Open the editor on a dedicated thread. Returns null when the plugin has no editor or the
    /// embed fails. Blocks until the window is up or has failed.</summary>
    public static EditorWindow? Open(IPluginGui? gui, string title)
    {
        if (gui is not { HasEditor: true }) return null;
        var w = new EditorWindow(gui, title);
        w._thread.Start();
        w._ready.Wait();
        if (w._session is not { IsOpen: true }) { w.Close(); return null; }
        return w;
    }

    public ulong WindowHandle => _session?.WindowHandle ?? 0;
    public bool IsOpen => _running && _session is { IsOpen: true };
    public string? Error => _error ?? _session?.Error;
    public uint EmbeddedChildCount => _session?.EmbeddedChildCount ?? 0;

    private void Run()
    {
        try
        {
            _session = EditorSession.Open(_gui, _title);
            if (_session is not { IsOpen: true }) { _ready.Set(); return; }
            _running = true;
            _ready.Set();
            while (_running && !_session.ShouldClose)
                _session.PumpOnce(30);
        }
        catch (Exception ex) { _error = ex.Message; _ready.Set(); }
        finally { _session?.Close(); _running = false; }
    }

    /// <summary>Close the editor and its window; blocks until the editor thread has torn everything down.</summary>
    public void Close()
    {
        _running = false;
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(2000);
    }

    public void Dispose() => Close();
}
