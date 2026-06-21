using System;
using System.Runtime.InteropServices;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out TestRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct TestRect { public int Left, Top, Right, Bottom; }
    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WM_CLOSE = 0x0010;

    [Fact]
    public void WmClose_latches_ShouldClose()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");

        using var window = EditorPlatform.Current.CreateWindow("Win32 close test", 320, 240);
        Assert.NotNull(window);
        window!.Map();
        Assert.False(window.ShouldClose);

        Assert.True(PostMessageW((IntPtr)window.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero));
        for (var i = 0; i < 20 && !window.ShouldClose; i++) window.PumpOnce(10);
        Assert.True(window.ShouldClose, "WM_CLOSE should latch ShouldClose.");
    }

    [Fact]
    public void Resize_tracks_the_embedded_child()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");

        using var window = EditorPlatform.Current.CreateWindow("Win32 resize test", 320, 240);
        Assert.NotNull(window);
        window!.Map();

        // Embed a child the way a plugin would, then resize the host and confirm the child tracks it.
        var child = CreateWindowExW(0, "STATIC", null, WS_CHILD | WS_VISIBLE, 0, 0, 100, 100,
            (IntPtr)window.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, child);
        try
        {
            window.Resize(500, 400);
            window.PumpOnce(10);
            Assert.True(GetClientRect(child, out var r));
            Assert.Equal(500, r.Right - r.Left);
            Assert.Equal(400, r.Bottom - r.Top);
        }
        finally { DestroyWindow(child); }
    }
}
