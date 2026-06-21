using System;
using System.Threading;
using MidiSharp.Hosting.EditorHost;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Live Win32 embed through the full EditorSession sequence: open a real top-level window and embed the
/// managed fake editor's child HWND into it, then confirm the host actually parented a child window. The
/// Windows analogue of <see cref="ClapEditorWindowTests"/>; self-skips off Windows or on a non-interactive
/// desktop. No native fixture needed.
/// </summary>
[Collection("EditorWindows")]
public sealed class Win32EditorWindowTests
{
    [Fact]
    public void Embeds_the_fake_editor_in_a_native_window()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");

        var gui = new FakeEditorGui(320, 240);
        using var window = EditorWindow.Open(gui, "MidiSharp Win32 editor test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor window should be open (error: {window.Error}).");
        Assert.NotEqual(0UL, window.WindowHandle);

        // The fake's SetParent created a child of our host window — give it a moment, then verify.
        uint children = 0;
        for (var i = 0; i < 20 && children == 0; i++) { children = window.EmbeddedChildCount; if (children == 0) Thread.Sleep(50); }
        Assert.True(children >= 1, "the fake editor should have embedded a child window into the host window.");

        window.Close();
        Assert.False(window.IsOpen);
    }
}
