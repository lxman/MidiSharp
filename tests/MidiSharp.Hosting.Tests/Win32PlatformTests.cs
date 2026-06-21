using System;
using MidiSharp.Hosting.EditorHost;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// The Win32 windowing backend at the platform/window level (no plugin): EditorPlatform selects it on
/// Windows, a host window is created with a real HWND and an empty child set, resize doesn't throw, and the
/// run loop pumps. Self-skips on a non-interactive desktop.
/// </summary>
[Collection("EditorWindows")]
public sealed class Win32PlatformTests
{
    [Fact]
    public void Creates_a_host_window_with_a_handle_and_pumps()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");

        using var window = EditorPlatform.Current.CreateWindow("Win32 platform test", 320, 240);
        Assert.NotNull(window);
        Assert.Equal("win32", window!.WindowApi);
        Assert.NotEqual(0UL, window.Handle);
        Assert.False(window.ShouldClose);
        Assert.Equal(0u, window.EmbeddedChildCount);   // nothing embedded yet

        window.Map();
        window.Resize(400, 300);     // must not throw with no child
        window.PumpOnce(10);         // exercises the run loop's message pump
        Assert.False(window.ShouldClose);
    }
}
