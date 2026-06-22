using System;
using System.Linq;
using System.Threading;
using MidiSharp.Hosting.Clap;
using MidiSharp.Hosting.EditorHost;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Live Win32 embed of the clean-room CLAP gui fixture's editor (set_parent creates a child HWND). CLAP
/// passes the window API straight through, so this verifies the win32 path with no adapter change. Self-skips
/// off Windows, on a non-interactive desktop, or when the fixture hasn't been built (build-fixtures.ps1).
/// </summary>
[Collection("EditorWindows")]
public sealed class ClapWindowsEditorTests
{
    private static readonly AudioConfig Config = new(48000, 512, ChannelCount: 2);
    private readonly ClapFormat _format = new();

    private IHostedPlugin? LoadFixture()
    {
        if (!WinFixtures.Available) return null;
        PluginDescriptor? d = _format.Scan([WinFixtures.Dir]).FirstOrDefault(p => p.Id == "midisharp.test.gui");
        return d == null ? null : _format.Load(d, Config);
    }

    [Fact]
    public void Embeds_the_clap_fixture_editor_in_a_native_window()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");
        IHostedPlugin? plugin = LoadFixture();
        Assert.SkipWhen(plugin == null, "CLAP win32 gui fixture not built.");
        using IHostedPlugin _ = plugin;

        IPluginGui? gui = plugin!.Gui;
        Assert.NotNull(gui);
        Assert.True(gui!.HasEditor);
        Assert.True(gui.IsApiSupported("win32", floating: false), "the CLAP fixture editor should support win32.");
        Assert.True(gui.TryGetSize(out int w, out int h));
        Assert.Equal(320, w);
        Assert.Equal(240, h);

        using EditorWindow? window = EditorWindow.Open(gui, "CLAP win32 editor test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor window should open (error: {window.Error}).");

        uint children = 0;
        for (var i = 0; i < 20 && children == 0; i++) { children = window.EmbeddedChildCount; if (children == 0) Thread.Sleep(50); }
        Assert.True(children >= 1, "the CLAP fixture should have embedded a child window via set_parent(win32).");
        window.Close();
    }
}
