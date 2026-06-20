using System;
using System.IO;
using System.Linq;
using MidiSharp.Hosting;
using MidiSharp.Hosting.Vst2;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// VST2 adapter: structural checks plus a live gate against the clean-room gain fixture
/// (midisharp_gain_vst2.so, uniqueID 'MsG2'). Verifies the AEffect ABI end to end — VSTPluginMain +
/// audioMaster handshake, setSampleRate/blockSize/resume, planar processReplacing, 0..1 parameters, and
/// sample-accurate MIDI via effProcessEvents/deltaFrames. Self-skips when the fixture isn't installed.
/// </summary>
public sealed class Vst2Tests
{
    private const int Rate = 48000;
    private const int Block = 512;
    private static readonly AudioConfig Config = new(Rate, Block, ChannelCount: 2);

    private readonly Vst2Format _format = new();
    private readonly ITestOutputHelper _out;

    public Vst2Tests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Reports_name_and_paths_and_tolerates_a_missing_dir()
    {
        Assert.Equal("VST2", _format.Name);
        Assert.NotEmpty(_format.DefaultSearchPaths);
        Assert.Empty(_format.Scan([Path.Combine(Path.GetTempPath(), "midisharp-no-vst-here")]).ToList());
    }

    private IHostedPlugin? LoadGain()
    {
        var d = _format.Scan(_format.DefaultSearchPaths).FirstOrDefault(p => p.Name == "MidiSharp VST2 Gain");
        return d == null ? null : _format.Load(d, Config);
    }

    [Fact]
    public void Loads_the_vst2_fixture_and_reads_its_metadata()
    {
        var plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST2 gain fixture not installed.");
        using var _ = plugin;
        _out.WriteLine($"Loaded {plugin!.Descriptor.Name} ({plugin.Descriptor.Vendor}), {plugin.Parameters.Count} params, instrument={plugin.IsInstrument}");
        Assert.Equal("MidiSharp VST2 Gain", plugin.Descriptor.Name);
        Assert.Single(plugin.Parameters);
        Assert.Equal("Gain", plugin.Parameters[0].Name);
        Assert.False(plugin.IsInstrument);
    }

    [Fact]
    public void Applies_its_parameter_through_the_bridge()
    {
        var plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST2 gain fixture not installed.");
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

    [Fact]
    public void Midi_cc_event_applies_sample_accurately()
    {
        var plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST2 gain fixture not installed.");
        using var effect = new HostedEffect(plugin!, Config);

        float[] RunBlock(Action<HostedEffect> queue)
        {
            var buf = new float[Block * 2];
            Array.Fill(buf, 1f);
            queue(effect);
            effect.Process(buf);
            var l = new float[Block];
            for (var i = 0; i < Block; i++) l[i] = buf[2 * i];
            return l;
        }

        RunBlock(e => e.Plugin.SetParameter(0, 0.5));   // establish ×1
        var warm = RunBlock(_ => { });
        Assert.True(Math.Abs(warm[0] - 1f) < 1e-4, "warm-up should be ×1");

        // CC#7 = 127 at sample 300 → param 1.0 → ×2, exactly at that sample (deltaFrames).
        const int off = 300;
        var l = RunBlock(e => e.QueueEvent(HostEvent.Midi(off, 0xB0, 7, 127)));
        Assert.True(Math.Abs(l[off - 1] - 1f) < 1e-4, $"before CC should be ×1 (got {l[off - 1]})");
        Assert.True(Math.Abs(l[off] - 2f) < 1e-4, $"at CC should be ×2 (got {l[off]})");
        var first = -1;
        for (var i = 0; i < l.Length; i++) if (Math.Abs(l[i] - 1f) > 1e-4) { first = i; break; }
        Assert.Equal(off, first);
        _out.WriteLine($"VST2 MIDI CC step at sample {first} (want {off})");
    }
}
