namespace MidiSharp.Hosting;

/// <summary>
/// A plugin's native editor window, exposed format-agnostically. The methods mirror the embed sequence the
/// windowing-system extensions define (CLAP <c>clap.gui</c>, VST3 <c>IPlugView</c>): query the preferred
/// size and which windowing API works, then <see cref="Create"/> the editor, parent it into a host window
/// (<see cref="SetParent"/>) or float it, and <see cref="Show"/> it. A host that owns a native window (the
/// editor-host process) drives this directly; an out-of-process host proxies the calls to the worker that
/// holds the live plugin instance.
/// </summary>
/// <remarks>
/// Editor calls are main-thread only (never the audio thread). Sizes are in physical pixels on X11/Win32.
/// </remarks>
public interface IPluginGui
{
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
