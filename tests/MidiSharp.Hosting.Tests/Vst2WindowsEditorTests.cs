using System;
using System.Linq;
using System.Threading;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.Vst2;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Live Win32 embed of the clean-room VST2 gain fixture's editor (effEditOpen creates a child HWND).
/// Self-skips off Windows, on a non-interactive desktop, or when the fixture hasn't been built
/// (tests/fixtures/win/build-fixtures.ps1).
/// </summary>
[Collection("EditorWindows")]
public sealed class Vst2WindowsEditorTests
{
    private static readonly AudioConfig Config = new(48000, 512, ChannelCount: 2);
    private readonly Vst2Format _format = new();

    private IHostedPlugin? LoadFixture()
    {
        if (!WinFixtures.Available) return null;
        PluginDescriptor? d = _format.Scan([WinFixtures.Dir]).FirstOrDefault(p => p.Name == "MidiSharp VST2 Gain");
        return d == null ? null : _format.Load(d, Config);
    }

    [Fact]
    public void Embeds_the_vst2_fixture_editor_in_a_native_window()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");
        IHostedPlugin? plugin = LoadFixture();
        Assert.SkipWhen(plugin == null, "VST2 win32 fixture not built.");
        using IHostedPlugin _ = plugin;

        IPluginGui? gui = plugin!.Gui;
        Assert.NotNull(gui);
        Assert.True(gui!.HasEditor);
        Assert.True(gui.IsApiSupported("win32", floating: false), "the fixture editor should support win32.");
        Assert.True(gui.TryGetSize(out int w, out int h));
        Assert.Equal(300, w);
        Assert.Equal(200, h);

        using EditorWindow? window = EditorWindow.Open(gui, "VST2 win32 editor test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor window should open (error: {window.Error}).");

        uint children = 0;
        for (var i = 0; i < 20 && children == 0; i++) { children = window.EmbeddedChildCount; if (children == 0) Thread.Sleep(50); }
        Assert.True(children >= 1, "effEditOpen should have created a child window in the host window.");
        window.Close();
    }
}
