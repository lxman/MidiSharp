using System;
using System.Linq;
using MidiSharp.Hosting;
using MidiSharp.Hosting.AudioUnit;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// The AppKit-free, no-instantiation slice of AU v3 support: discovery. Apple's component registry surfaces v3
/// AUs alongside v2 ones (same <c>AudioComponentFindNext</c> walk), so a real installed v3 effect/instrument
/// appears in <see cref="AudioUnitFormat.Scan"/> with its <c>type:subtype:manufacturer</c> id. SKIPs when the
/// unit isn't installed, so CI/other machines stay green.
/// </summary>
/// <remarks>
/// <b>Loading</b> a v3 AU is deliberately NOT tested here. v3 instantiation is asynchronous
/// (<c>AudioComponentInstantiate</c>) and its completion is delivered on the <i>main</i> dispatch queue, which
/// xUnit's pool threads can't drain — so the async-load branch, the OOP render shim, the instrument note, and the
/// state round-trip are proven on thread 0 by <c>MidiSharp.Hosting.MacEditorHarness</c> (the same main-thread
/// harness the Cocoa editor uses), not here. This mirrors the v2 split: discovery in xUnit, embedding in the
/// harness.
/// </remarks>
public sealed class AudioUnitV3Tests
{
    [Fact]
    public void Discovers_the_dimchorus_v3_effect()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Audio Units are macOS-only.");

        var format = new AudioUnitFormat();
        PluginDescriptor? dimChorus = format.Scan(format.DefaultSearchPaths)
            .FirstOrDefault(d => d.Id.StartsWith("aufx:DIMC", StringComparison.Ordinal));
        Assert.SkipWhen(dimChorus is null, "v3 effect DimChorus not installed.");

        Assert.Equal("AU", dimChorus!.Format);
        Assert.False(dimChorus.IsInstrument, "DimChorus is an effect (aufx).");
    }

    [Fact]
    public void Discovers_the_audmod_v3_instrument()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Audio Units are macOS-only.");

        var format = new AudioUnitFormat();
        PluginDescriptor? audMod = format.Scan(format.DefaultSearchPaths)
            .FirstOrDefault(d => d.Id.StartsWith("aumu:audM", StringComparison.Ordinal));
        Assert.SkipWhen(audMod is null, "v3 instrument AudMod not installed.");

        Assert.Equal("AU", audMod!.Format);
        Assert.True(audMod.IsInstrument, "AudMod is a music device (aumu) → an instrument.");
    }
}
