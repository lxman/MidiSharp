using System;
using System.Linq;
using System.Threading;
using MidiSharp.Hosting;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.Vst3;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Live Win32 embed of a REAL VST3 plugin's editor (u-he Podolski/Protoverb): confirms the adapter accepts
/// the "win32" window API ("HWND") and the plugin parents a child window into our host HWND. Self-skips off
/// Windows, on a non-interactive desktop, or when no target plugin is installed.
/// </summary>
[Collection("EditorWindows")]
public sealed class Vst3WindowsEditorTests
{
    private static readonly AudioConfig Config = new(48000, 512, ChannelCount: 2);
    private readonly Vst3Format _format = new();

    // A real, editor-bearing VST3 to target by name (well-behaved commercial plugins; loaded in-process).
    private PluginDescriptor? FindTarget() => _format.Scan(_format.DefaultSearchPaths)
        .FirstOrDefault(p => p.Name.Contains("Podolski", StringComparison.OrdinalIgnoreCase)
                          || p.Name.Contains("Protoverb", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Embeds_a_real_vst3_editor_in_a_native_window()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");
        var desc = FindTarget();
        Assert.SkipWhen(desc == null, "no target VST3 plugin (Podolski/Protoverb) installed.");

        using var plugin = _format.Load(desc!, Config);
        Assert.True(plugin.Gui is { HasEditor: true }, "the target VST3 should expose an editor.");
        Assert.True(plugin.Gui!.IsApiSupported("win32", floating: false), "the VST3 editor should support win32 (HWND).");

        using var window = EditorWindow.Open(plugin.Gui, "VST3 win32 editor test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor window should open (error: {window.Error}).");

        uint children = 0;
        for (var i = 0; i < 40 && children == 0; i++) { children = window.EmbeddedChildCount; if (children == 0) Thread.Sleep(50); }
        Assert.True(children >= 1, "the VST3 plugin should have embedded a child window via attached(HWND).");
        window.Close();
    }
}
