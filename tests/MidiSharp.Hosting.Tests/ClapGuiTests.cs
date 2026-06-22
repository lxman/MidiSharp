using System.Linq;
using MidiSharp.Hosting.Clap;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// The CLAP native-editor capability layer (clap.gui), queried without mapping a window — so it runs
/// headless. The gui fixture (midisharp.test.gui) reports an X11 editor at 320×240; the gain fixture has
/// no editor. Self-skips when the gui fixture isn't installed.
/// </summary>
public sealed class ClapGuiTests
{
    private static readonly AudioConfig Config = new(48000, 512, ChannelCount: 2);
    private readonly ClapFormat _format = new();

    private IHostedPlugin? Load(string id)
    {
        PluginDescriptor? d = _format.Scan(_format.DefaultSearchPaths).FirstOrDefault(p => p.Id == id);
        return d == null ? null : _format.Load(d, Config);
    }

    [Fact]
    public void Plugin_with_an_editor_reports_its_gui_capability_and_size()
    {
        IHostedPlugin? plugin = Load("midisharp.test.gui");
        Assert.SkipWhen(plugin == null, "CLAP gui fixture not installed.");
        using IHostedPlugin _ = plugin;

        IPluginGui? gui = plugin!.Gui;
        Assert.NotNull(gui);
        Assert.True(gui!.HasEditor);
        Assert.True(gui.IsApiSupported("x11", floating: false), "the editor should support embedded X11.");
        Assert.False(gui.IsApiSupported("win32", floating: false), "win32 isn't available on Linux.");
        Assert.True(gui.TryGetSize(out int w, out int h));
        Assert.Equal(320, w);
        Assert.Equal(240, h);
    }

    [Fact]
    public void Plugin_without_an_editor_exposes_no_gui()
    {
        IHostedPlugin? plugin = Load("midisharp.test.gain");
        Assert.SkipWhen(plugin == null, "CLAP gain fixture not installed.");
        using IHostedPlugin _ = plugin;
        Assert.Null(plugin!.Gui);
    }
}
