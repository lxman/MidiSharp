using System;
using System.Linq;
using System.Threading;
using MidiSharp.Hosting.Clap;
using MidiSharp.Hosting.EditorHost;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Live native-editor embed: open a real top-level X11 window and embed the CLAP gui fixture's editor into
/// it, then confirm the plugin actually parented a child window (XQueryTree). Self-skips with no display
/// (headless CI) or no fixture, so it only runs where a window can truly be mapped (here: Xwayland).
/// </summary>
[Collection("EditorWindows")]
public sealed class ClapEditorWindowTests
{
    private static readonly AudioConfig Config = new(48000, 512, ChannelCount: 2);

    [Fact]
    public void Embeds_the_plugin_editor_in_a_native_window()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")), "no X display.");
        var fmt = new ClapFormat();
        PluginDescriptor? d = fmt.Scan(fmt.DefaultSearchPaths).FirstOrDefault(p => p.Id == "midisharp.test.gui");
        Assert.SkipWhen(d == null, "CLAP gui fixture not installed.");

        using IHostedPlugin plugin = fmt.Load(d!, Config);
        using EditorWindow? window = EditorWindow.Open(plugin.Gui, "MidiSharp editor test");
        Assert.NotNull(window);                       // open failed → null (error in window?.Error)
        Assert.True(window!.IsOpen, $"editor window should be open (error: {window.Error}).");
        Assert.NotEqual(0UL, window.WindowHandle);

        // The plugin's set_parent created an X11 child of our window — give the server a moment, then verify.
        uint children = 0;
        for (var i = 0; i < 20 && children == 0; i++) { children = window.EmbeddedChildCount; if (children == 0) Thread.Sleep(50); }
        Assert.True(children >= 1, "the plugin should have embedded a child window into the host window.");

        window.Close();
        Assert.False(window.IsOpen);
    }

    [Fact]
    public void Host_run_loop_drives_the_editors_timer()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")), "no X display.");
        var fmt = new ClapFormat();
        PluginDescriptor? d = fmt.Scan(fmt.DefaultSearchPaths).FirstOrDefault(p => p.Id == "midisharp.test.gui");
        Assert.SkipWhen(d == null, "CLAP gui fixture not installed.");

        using IHostedPlugin plugin = fmt.Load(d!, Config);
        // The fixture registers a 20 ms timer via clap.timer-support on set_parent and counts on_timer calls,
        // exposed as the first 8 bytes of clap.state. If the host pumps the plugin's timer, it climbs.
        using EditorWindow? window = MidiSharp.Hosting.EditorHost.EditorWindow.Open(plugin.Gui, "CLAP run-loop test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor should open (error: {window.Error}).");

        Thread.Sleep(400);
        byte[] state = plugin.SaveState();
        window.Close();

        Assert.True(state.Length >= 8, "fixture state should carry the tick count.");
        var ticks = BitConverter.ToDouble(state, 0);
        Assert.True(ticks > 3, $"the host run loop should have fired the editor's clap timer several times (ticks={ticks}).");
    }
}
