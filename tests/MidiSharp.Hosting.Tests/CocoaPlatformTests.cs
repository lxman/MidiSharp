using System;
using MidiSharp.Hosting.EditorHost;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// The Cocoa backend at the platform level, exercising what xUnit can (it runs on a pool thread, not the main
/// thread, so it cannot create a real AppKit window — that gate is the MacEditorHarness). Verifies the backend
/// reports availability without throwing on any thread, and that it declines to create a window off the main
/// thread (the fail-safe guard that keeps the in-process/sandbox-off path from crashing on macOS). macOS-only.
/// </summary>
[Collection("EditorWindows")]
public sealed class CocoaPlatformTests
{
    [Fact]
    public void Declines_window_creation_off_the_main_thread()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Cocoa backend is macOS-only.");

        var platform = new CocoaPlatform();
        _ = platform.IsAvailable;   // must not throw on a non-main thread (uses CGMainDisplayID, not AppKit)

        // xUnit runs this on a pool thread. AppKit is main-thread-only, so the backend must return null rather
        // than build a window off the main thread — turning the in-process path into a clean no-op on macOS.
        INativeEditorWindow? window = platform.CreateWindow("off-main", 320, 240);
        Assert.Null(window);
    }
}
