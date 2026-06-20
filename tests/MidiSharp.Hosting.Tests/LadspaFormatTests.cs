using System.IO;
using System.Linq;
using MidiSharp.Hosting;
using MidiSharp.Hosting.Ladspa;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Structural checks for the LADSPA adapter that don't need a native plugin (none is installed at
/// scaffold time). Live load/run verification against a real <c>.so</c> (e.g. swh-plugins) is the
/// Phase-0 acceptance gate, tracked separately.
/// </summary>
public sealed class LadspaFormatTests
{
    [Fact]
    public void Reports_its_name_and_default_paths()
    {
        var format = new LadspaFormat();
        Assert.Equal("LADSPA", format.Name);
        Assert.NotEmpty(format.DefaultSearchPaths);
    }

    [Fact]
    public void Scanning_a_missing_directory_yields_nothing_and_does_not_throw()
    {
        var format = new LadspaFormat();
        var ghost = Path.Combine(Path.GetTempPath(), "midisharp-no-ladspa-here");
        var results = format.Scan([ghost]).ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void Registry_routes_load_by_format_name()
    {
        var registry = new PluginRegistry().Register(new LadspaFormat());
        Assert.Single(registry.Formats);
        Assert.Equal("LADSPA", registry.Formats[0].Name);
        // Rescan over no real paths is harmless and leaves the catalog empty.
        registry.Rescan([Path.Combine(Path.GetTempPath(), "midisharp-no-ladspa-here")]);
        Assert.Empty(registry.Plugins);
    }
}
