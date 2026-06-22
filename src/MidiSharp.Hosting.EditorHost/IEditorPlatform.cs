using System;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// A native host window a plugin editor is embedded into, abstracted away from any one windowing system.
/// Everything platform-specific about hosting an editor lives behind this: <see cref="EditorSession"/> drives
/// the format-agnostic embed sequence (bind run loop → create → parent into <see cref="Handle"/> → show) and
/// never touches X11/Win32/Cocoa directly. To add Windows or macOS support, implement this plus
/// <see cref="IEditorPlatform"/> and register it in <see cref="EditorPlatform.Current"/> — no other code changes.
/// </summary>
public interface INativeEditorWindow : IDisposable
{
    /// <summary>The windowing-API name to hand the plugin (<c>"x11"</c> / <c>"win32"</c> / <c>"cocoa"</c>).</summary>
    string WindowApi { get; }

    /// <summary>The native handle to parent the plugin's editor into (an X11 window id, an HWND, …).</summary>
    ulong Handle { get; }

    /// <summary>The run loop the plugin registers its fds/timers with; this window's events are pumped on it too.</summary>
    IEditorRunLoop RunLoop { get; }

    /// <summary>Resize the host window (and any embedded child) to the editor's reported size.</summary>
    void Resize(int width, int height);

    /// <summary>Make the window visible.</summary>
    void Map();

    /// <summary>Finish embedding once the plugin has parented its window (XEMBED notify on X11; no-op elsewhere).</summary>
    void CompleteEmbed();

    /// <summary>Number of windows the plugin has embedded — ≥1 once its editor is parented (for verification).</summary>
    uint EmbeddedChildCount { get; }

    /// <summary>True once the user asked the window manager to close the window.</summary>
    bool ShouldClose { get; }

    /// <summary>One event-loop iteration: poll this window's events plus the plugin's registered fds/timers.</summary>
    void PumpOnce(int maxWaitMs);
}

/// <summary>Creates native host windows for a windowing system. One per OS; selected by <see cref="EditorPlatform.Current"/>.</summary>
public interface IEditorPlatform
{
    /// <summary>Whether editors can actually be opened here (e.g. a display is present).</summary>
    bool IsAvailable { get; }

    /// <summary>Open a top-level host window of the given size, or null if it can't be created.</summary>
    INativeEditorWindow? CreateWindow(string title, int width, int height);
}

/// <summary>The windowing backend for the current OS. Swap-point for platform support.</summary>
public static class EditorPlatform
{
    private static IEditorPlatform? _current;

    public static IEditorPlatform Current => _current ??= Select();

    /// <summary>Override the backend (tests, or a host that supplies its own windowing).</summary>
    public static void Set(IEditorPlatform platform) => _current = platform;

    private static IEditorPlatform Select()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) return new X11Platform();
        if (OperatingSystem.IsWindows()) return new Win32Platform();
        if (OperatingSystem.IsMacOS()) return new CocoaPlatform();
        return new UnsupportedPlatform();
    }

    private sealed class UnsupportedPlatform : IEditorPlatform
    {
        public bool IsAvailable => false;
        public INativeEditorWindow? CreateWindow(string title, int width, int height) => null;
    }
}
