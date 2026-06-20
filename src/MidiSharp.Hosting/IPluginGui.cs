namespace MidiSharp.Hosting;

/// <summary>
/// The host services a plugin editor needs to drive itself: register file descriptors (its windowing-system
/// connection) and timers, and post work back. The editor host's UI thread owns one of these and pumps the
/// registrations alongside its own window events — VST3 <c>Linux::IRunLoop</c>, CLAP <c>clap.timer-support</c>
/// + <c>clap.posix-fd-support</c>, and VST2 <c>effEditIdle</c> all map onto it. All calls are on the UI thread.
/// </summary>
public interface IEditorRunLoop
{
    /// <summary>Poll <paramref name="fd"/> for readability; call <paramref name="onReady"/> on the UI thread when set.</summary>
    void RegisterFd(int fd, System.Action onReady);
    void UnregisterFd(int fd);

    /// <summary>Call <paramref name="onTick"/> every <paramref name="periodMs"/> ms on the UI thread, keyed by <paramref name="token"/>.</summary>
    void RegisterTimer(long periodMs, object token, System.Action onTick);
    void UnregisterTimer(object token);
}

/// <summary>
/// A plugin's native editor window, exposed format-agnostically. The methods mirror the embed sequence the
/// windowing-system extensions define (CLAP <c>clap.gui</c>, VST3 <c>IPlugView</c>): query the preferred
/// size and which windowing API works, then <see cref="Create"/> the editor, parent it into a host window
/// (<see cref="SetParent"/>) or float it, and <see cref="Show"/> it. A host that owns a native window (the
/// editor-host process) drives this directly; an out-of-process host proxies the calls to the worker that
/// holds the live plugin instance.
/// </summary>
/// <remarks>
/// Editor calls are main-thread only (never the audio thread), and — except <see cref="HasEditor"/> — must all
/// run on the <b>same</b> UI thread (the one that pumps the editor's run loop), since plugin GUI toolkits are
/// thread-affine. Sizes are in physical pixels on X11/Win32.
/// </remarks>
public interface IPluginGui
{
    /// <summary>Give the editor a run loop to register its fds/timers with, before <see cref="Create"/>. No-op for
    /// plugins that don't need one. Pass null on teardown.</summary>
    void BindRunLoop(IEditorRunLoop? runLoop) { }

    /// <summary>Per-tick idle for formats that repaint via a host idle call (VST2 <c>effEditIdle</c>); no-op otherwise.</summary>
    void Idle() { }

    /// <summary>True when the plugin provides an editor at all (the gui extension/interface is present).</summary>
    bool HasEditor { get; }

    /// <summary>Whether the plugin can present its editor through the given windowing API (e.g. "x11"),
    /// embedded (<paramref name="floating"/> false) or as its own floating window (true).</summary>
    bool IsApiSupported(string windowApi, bool floating);

    /// <summary>Allocate the editor's resources for a windowing API. Embedded editors must then be parented
    /// (<see cref="SetParent"/>); floating editors are managed by the plugin. Call <see cref="Show"/> after.</summary>
    bool Create(string windowApi, bool floating);

    /// <summary>Hint the editor's pixel scale (no-op for APIs that use logical pixels). Call before sizing.</summary>
    bool SetScale(double scale);

    /// <summary>The editor's preferred size in pixels. False if the plugin won't report one.</summary>
    bool TryGetSize(out int width, out int height);

    /// <summary>Embed the editor into a host window — <paramref name="windowHandle"/> is the platform handle
    /// (an X11 window XID on Linux). Call after <see cref="Create"/> with <c>floating: false</c>.</summary>
    bool SetParent(string windowApi, ulong windowHandle);

    /// <summary>Make the editor visible.</summary>
    bool Show();

    /// <summary>Hide the editor without freeing it.</summary>
    bool Hide();

    /// <summary>Free the editor's resources. After this the editor must be re-<see cref="Create"/>d to reopen.</summary>
    void Destroy();
}
