using System;
using System.Linq;
using MidiSharp.Hosting;
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
        var found = ScanAll();
        Assert.SkipWhen(found.Length == 0, "No CLAP plugins on the search path — install one to run this.");
        _out.WriteLine($"Discovered {found.Length} CLAP plugin(s):");
        foreach (var p in found.Take(25)) _out.WriteLine($"  [{p.Id}] {p.Name} ({p.Vendor}) instrument={p.IsInstrument}");
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
        var desc = ScanAll().FirstOrDefault(p => p.Id == targetId);
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
            foreach (var v in buf) Assert.True(float.IsFinite(v), $"{plugin.Descriptor.Name} produced a non-finite sample.");
        }
    }

    [Fact]
    public void Loads_a_clap_effect_and_applies_its_parameter()
    {
        var found = ScanAll();
        Assert.SkipWhen(found.Length == 0, "No CLAP plugins on the search path — install one to run this.");

        // Prefer the gain test fixture; else the first non-instrument plugin that loads as stereo.
        var ordered = found.OrderByDescending(p => p.Id.Contains("gain", StringComparison.OrdinalIgnoreCase))
                           .ThenBy(p => p.IsInstrument);
        IHostedPlugin? plugin = null;
        foreach (var desc in ordered)
        {
            try { plugin = _format.Load(desc, Config); break; }
            catch (NotSupportedException) { /* non-stereo main port — try next */ }
        }
        Assert.SkipWhen(plugin == null, "No stereo-compatible CLAP effect found.");

        using var effect = new HostedEffect(plugin!, Config);
        _out.WriteLine($"Loaded: {plugin!.Descriptor.Name}");
        foreach (var par in plugin.Parameters)
            _out.WriteLine($"  [{par.Index}] {par.Name} [{par.MinValue:0.###}..{par.MaxValue:0.###}] def {par.DefaultValue:0.###}");

        const double amp = 0.4;
        var inputRms = amp / Math.Sqrt(2);

        double RenderRms(int? paramIndex, double normalized)
        {
            if (paramIndex is { } pi) plugin.SetParameter(pi, normalized);
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
                foreach (var v in buf) { Assert.True(float.IsFinite(v), "non-finite sample"); sumSq += (double)v * v; n++; }
            }
            return Math.Sqrt(sumSq / n);
        }

        var gain = plugin.Parameters.FirstOrDefault(p => p.Name.Contains("gain", StringComparison.OrdinalIgnoreCase))
                   ?? plugin.Parameters.FirstOrDefault();

        if (gain == null)
        {
            // No parameters — at least prove the audio path runs and produces finite, non-silent output.
            var rms = RenderRms(null, 0);
            _out.WriteLine($"output RMS (no params): {rms:F5}");
            Assert.True(rms > 0, "Expected non-silent output.");
            return;
        }

        // A gain in [0..2]: normalized 0.5 → ×1 (transparent), 0.25 → ×0.5, 1.0 → ×2. The output level
        // must track the parameter — end-to-end proof of the param-event path and the process call.
        var rmsUnity = RenderRms(gain.Index, gain.Normalize(1.0));
        var rmsHalf = RenderRms(gain.Index, gain.Normalize(0.5));
        var rmsDouble = RenderRms(gain.Index, gain.Normalize(2.0));
        _out.WriteLine($"input={inputRms:F5}  unity={rmsUnity:F5}  half={rmsHalf:F5}  double={rmsDouble:F5}");

        Assert.True(Math.Abs(rmsUnity - inputRms) < 0.01, $"×1 gain should be transparent (got {rmsUnity:F5}).");
        Assert.True(Math.Abs(rmsHalf - inputRms * 0.5) < 0.01, $"×0.5 gain should halve (got {rmsHalf:F5}).");
        Assert.True(Math.Abs(rmsDouble - inputRms * 2.0) < 0.02, $"×2 gain should double (got {rmsDouble:F5}).");
    }
}
