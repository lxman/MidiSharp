using System;
using System.IO;
using System.Linq;
using MidiSharp.Hosting;
using MidiSharp.Hosting.Vst3;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// VST3 adapter: structural checks plus a live gate against the clean-room single-component gain fixture
/// (MidiSharpGain.vst3). Exercises the COM ABI end to end — bundle resolution, GetPluginFactory,
/// createInstance, IComponent/IAudioProcessor/IEditController, setupProcessing/activateBus/setActive, the
/// planar process call, and 0..1 parameters via the controller. Self-skips without the fixture.
/// </summary>
public sealed class Vst3Tests
{
    private const int Rate = 48000;
    private const int Block = 512;
    private static readonly AudioConfig Config = new(Rate, Block, ChannelCount: 2);

    private readonly Vst3Format _format = new();
    private readonly ITestOutputHelper _out;

    public Vst3Tests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Reports_name_and_paths_and_tolerates_a_missing_dir()
    {
        Assert.Equal("VST3", _format.Name);
        Assert.NotEmpty(_format.DefaultSearchPaths);
        Assert.Empty(_format.Scan([Path.Combine(Path.GetTempPath(), "midisharp-no-vst3-here")]).ToList());
    }

    private IHostedPlugin? LoadGain()
    {
        var d = _format.Scan(_format.DefaultSearchPaths).FirstOrDefault(p => p.Name == "MidiSharp VST3 Gain");
        return d == null ? null : _format.Load(d, Config);
    }

    [Fact]
    public void Loads_the_vst3_fixture_and_reads_its_metadata()
    {
        var plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST3 gain fixture not installed.");
        using var _ = plugin;
        _out.WriteLine($"Loaded {plugin!.Descriptor.Name}, {plugin.Parameters.Count} params");
        Assert.Equal("MidiSharp VST3 Gain", plugin.Descriptor.Name);
        Assert.Single(plugin.Parameters);
        Assert.Equal("Gain", plugin.Parameters[0].Name);
    }

    [Fact]
    public void Applies_its_parameter_through_the_bridge()
    {
        var plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST3 gain fixture not installed.");
        using var effect = new HostedEffect(plugin!, Config);

        const double amp = 0.4;
        var inputRms = amp / Math.Sqrt(2);

        double RenderRms(double normalized)
        {
            plugin!.SetParameter(0, normalized);
            effect.Reset();
            double sumSq = 0; long n = 0; var phase = 0.0;
            var buf = new float[Block * 2];
            for (var b = 0; b < 16; b++)
            {
                for (var i = 0; i < Block; i++)
                {
                    var s = (float)(amp * Math.Sin(phase));
                    phase += 2 * Math.PI * 1000.0 / Rate;
                    buf[2 * i] = s; buf[2 * i + 1] = s;
                }
                effect.Process(buf);
                foreach (var v in buf) { Assert.True(float.IsFinite(v), "non-finite"); sumSq += (double)v * v; n++; }
            }
            return Math.Sqrt(sumSq / n);
        }

        var unity = RenderRms(0.5);   // param 0.5 → ×1
        var half = RenderRms(0.25);   // param 0.25 → ×0.5
        var dbl = RenderRms(1.0);     // param 1.0 → ×2
        _out.WriteLine($"input={inputRms:F5} unity={unity:F5} half={half:F5} double={dbl:F5}");
        Assert.True(Math.Abs(unity - inputRms) < 0.01, $"×1 (got {unity:F5})");
        Assert.True(Math.Abs(half - inputRms * 0.5) < 0.01, $"×0.5 (got {half:F5})");
        Assert.True(Math.Abs(dbl - inputRms * 2.0) < 0.02, $"×2 (got {dbl:F5})");
    }
}
