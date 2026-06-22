using System.IO;
using System.Linq;
using MidiSharp.Hosting.Clap;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>Structural checks for the CLAP adapter that don't require a plugin on disk.</summary>
public sealed class ClapFormatTests
{
    [Fact]
    public void Reports_its_name_and_default_paths()
    {
        var format = new ClapFormat();
        Assert.Equal("CLAP", format.Name);
        Assert.NotEmpty(format.DefaultSearchPaths);
    }

    [Fact]
    public void Scanning_a_missing_directory_yields_nothing_and_does_not_throw()
    {
        var format = new ClapFormat();
        string ghost = Path.Combine(Path.GetTempPath(), "midisharp-no-clap-here");
        Assert.Empty(format.Scan([ghost]).ToList());
    }

    [Fact]
    public void Registry_registers_the_clap_format()
    {
        PluginRegistry registry = new PluginRegistry().Register(new ClapFormat());
        Assert.Single(registry.Formats);
        Assert.Equal("CLAP", registry.Formats[0].Name);
    }
}
