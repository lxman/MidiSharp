using System;
using System.Linq;
using MidiSharp.Hosting.Clap;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Phase-1 live acceptance gate: scan, load, and run a REAL native CLAP plugin through
/// <see cref="HostedEffect"/>, exercising the whole host ABI path — factory enumeration, create_plugin
/// with our clap_host, init/activate/start_processing, the audio-ports and params extensions,
/// param_value event delivery, and the planar process call. Self-skips when no CLAP plugin is present
/// (so it's inert in CI); locally it runs against whatever is on the CLAP path.
/// </summary>
public sealed class ClapLiveTests
{
    private const int Rate = 48000;
    private const int Block = 512;
    private static readonly AudioConfig Config = new(Rate, Block, ChannelCount: 2);

    private readonly ClapFormat _format = new();
    private readonly ITestOutputHelper _out;

    public ClapLiveTests(ITestOutputHelper output) => _out = output;

    private PluginDescriptor[] ScanAll() => _format.Scan(_format.DefaultSearchPaths).ToArray();

    [Fact]
    public void Scans_real_clap_plugins()
    {
        PluginDescriptor[] found = ScanAll();
        Assert.SkipWhen(found.Length == 0, "No CLAP plugins on the search path — install one to run this.");
        _out.WriteLine($"Discovered {found.Length} CLAP plugin(s):");
        foreach (PluginDescriptor p in found.Take(25)) _out.WriteLine($"  [{p.Id}] {p.Name} ({p.Vendor}) instrument={p.IsInstrument}");
        Assert.All(found, p => Assert.False(string.IsNullOrWhiteSpace(p.Id)));
    }

    // A single, specific, simple third-party effect (DISTRHO MaBitcrush) — proof the host ABI works with
    // real-world plugins, not just our fixture. Targeted by id (not "load any plugin"): loading arbitrary
    // untrusted plugins in-process can segfault, which no managed try/catch can contain — that robustness
    // is Phase 8 (out-of-process sandboxing), not this gate.
    [Fact]
    public void Loads_a_specific_real_clap_effect_and_processes_audio()
    {
        const string targetId = "studio.kx.distrho.MaBitcrush";
        PluginDescriptor? desc = ScanAll().FirstOrDefault(p => p.Id == targetId);
        Assert.SkipWhen(desc == null, $"{targetId} not installed.");

        IHostedPlugin plugin;
        try { plugin = _format.Load(desc!, Config); }
        catch (NotSupportedException) { Assert.Skip($"{targetId} is not a stereo effect."); return; }

        using var effect = new HostedEffect(plugin, Config);
        _out.WriteLine($"Loaded real plugin: {plugin.Descriptor.Name} ({plugin.Descriptor.Vendor}), {plugin.Parameters.Count} params");

        var buf = new float[Block * 2];
        var phase = 0.0;
        for (var b = 0; b < 8; b++)
        {
            for (var i = 0; i < Block; i++)
            {
                var s = (float)(0.3 * Math.Sin(phase));
                phase += 2 * Math.PI * 440.0 / Rate;
                buf[2 * i] = s; buf[2 * i + 1] = s;
            }
            effect.Process(buf);
            foreach (float v in buf) Assert.True(float.IsFinite(v), $"{plugin.Descriptor.Name} produced a non-finite sample.");
        }
    }

    // Phase-3 gate: a parameter-automation event and a MIDI event delivered at a sample offset within a
    // block take effect at exactly that sample, not at the block boundary. The gain fixture applies gain
    // changes sample-accurately, so a constant input makes the timing directly observable in the output.
    [Fact]
    public void Param_and_midi_events_apply_sample_accurately()
    {
        PluginDescriptor? desc = ScanAll().FirstOrDefault(p => p.Id == "midisharp.test.gain");
        Assert.SkipWhen(desc == null, "gain fixture not installed.");
        using var effect = new HostedEffect(_format.Load(desc!, Config), Config);

        // Process one block of constant-1.0 input (so output[i] == gain at sample i) and return channel L.
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
        int FirstChange(float[] x) { for (var i = 0; i < x.Length; i++) if (Math.Abs(x[i] - 1f) > 1e-4) return i; return -1; }

        // Establish gain = ×1 (normalized 0.5 of the 0..2 range) via a live set.
        float[] warm = RunBlock(e => e.Plugin.SetParameter(0, 0.5));
        Assert.True(Math.Abs(warm[0] - 1f) < 1e-4 && Math.Abs(warm[Block - 1] - 1f) < 1e-4, "warm-up gain should be ×1");

        // Parameter event at sample 256 → ×0.5.
        const int pOff = 256;
        float[] pOut = RunBlock(e => e.QueueEvent(HostEvent.Param(pOff, 0, 0.25)));
        Assert.True(Math.Abs(pOut[pOff - 1] - 1f) < 1e-4, $"sample before param event should be ×1 (got {pOut[pOff - 1]})");
        Assert.True(Math.Abs(pOut[pOff] - 0.5f) < 1e-4, $"sample at param event should be ×0.5 (got {pOut[pOff]})");
        Assert.Equal(pOff, FirstChange(pOut));

        RunBlock(e => e.Plugin.SetParameter(0, 0.5));   // back to ×1

        // MIDI CC#7 = 127 at sample 384 → ×2.
        const int mOff = 384;
        float[] mOut = RunBlock(e => e.QueueEvent(HostEvent.Midi(mOff, 0xB0, 7, 127)));
        Assert.True(Math.Abs(mOut[mOff - 1] - 1f) < 1e-4, $"sample before CC event should be ×1 (got {mOut[mOff - 1]})");
        Assert.True(Math.Abs(mOut[mOff] - 2f) < 1e-4, $"sample at CC event should be ×2 (got {mOut[mOff]})");
        Assert.Equal(mOff, FirstChange(mOut));

        _out.WriteLine($"param step at sample {FirstChange(pOut)} (want {pOff}); midi step at sample {FirstChange(mOut)} (want {mOff})");
    }

    [Fact]
    public void Loads_a_clap_effect_and_applies_its_parameter()
    {
        // Target the known gain fixture by id. Loading an ARBITRARY real plugin in-process is unsafe — it
        // can hang or segfault on load/teardown (that robustness is the out-of-process sandbox's job; see
        // Loads_a_specific_real_clap_effect_and_processes_audio). The fixture's [0..2] linear gain is also
        // what the ratio math below assumes.
        PluginDescriptor? desc = ScanAll().FirstOrDefault(p => p.Id.Equals("midisharp.test.gain", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(desc == null, "gain fixture (midisharp.test.gain) not installed.");

        IHostedPlugin plugin;
        try { plugin = _format.Load(desc!, Config); }
        catch (NotSupportedException) { Assert.Skip("gain fixture is not a stereo effect."); return; }

        using var effect = new HostedEffect(plugin, Config);
        PluginParameter? gain = plugin.Parameters.FirstOrDefault(p => p.Name.Contains("gain", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(gain == null, "gain fixture exposes no gain parameter.");

        const double amp = 0.4;
        double inputRms = amp / Math.Sqrt(2);

        double RenderRms(double normalized)
        {
            plugin.SetParameter(gain!.Index, normalized);
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
                foreach (float v in buf) { Assert.True(float.IsFinite(v), "non-finite sample"); sumSq += (double)v * v; n++; }
            }
            return Math.Sqrt(sumSq / n);
        }

        double rmsUnity = RenderRms(gain!.Normalize(1.0));
        double rmsHalf = RenderRms(gain.Normalize(0.5));
        double rmsDouble = RenderRms(gain.Normalize(2.0));
        _out.WriteLine($"input={inputRms:F5}  unity={rmsUnity:F5}  half={rmsHalf:F5}  double={rmsDouble:F5}");

        Assert.True(Math.Abs(rmsUnity - inputRms) < 0.01, $"×1 gain should be transparent (got {rmsUnity:F5}).");
        Assert.True(Math.Abs(rmsHalf - inputRms * 0.5) < 0.01, $"×0.5 gain should halve (got {rmsHalf:F5}).");
        Assert.True(Math.Abs(rmsDouble - inputRms * 2.0) < 0.02, $"×2 gain should double (got {rmsDouble:F5}).");
    }
}
