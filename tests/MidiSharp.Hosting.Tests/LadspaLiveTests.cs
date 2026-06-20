using System;
using System.Linq;
using MidiSharp.Hosting;
using MidiSharp.Hosting.Ladspa;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Phase-0 live acceptance gate: scan, load, and run a REAL native LADSPA plugin through
/// <see cref="HostedEffect"/>, measuring the result. Self-skips when no LADSPA plugins are installed
/// (so it's inert in CI); locally it runs against whatever is on the LADSPA search path (e.g. the
/// tap-plugins built into ~/.ladspa).
/// </summary>
public sealed class LadspaLiveTests
{
    private const int Rate = 48000;
    private const int Block = 512;
    private static readonly AudioConfig Config = new(Rate, Block, ChannelCount: 2);

    private readonly LadspaFormat _format = new();
    private readonly ITestOutputHelper _out;

    public LadspaLiveTests(ITestOutputHelper output) => _out = output;

    private PluginDescriptor[] ScanAll()
        => _format.Scan(_format.DefaultSearchPaths).ToArray();

    [Fact]
    public void Scans_real_plugins_from_the_ladspa_path()
    {
        var found = ScanAll();
        Assert.SkipWhen(found.Length == 0, "No LADSPA plugins on the search path — install some to run this.");

        _out.WriteLine($"Discovered {found.Length} LADSPA plugin(s):");
        foreach (var p in found.Take(25))
            _out.WriteLine($"  [{p.Id}] {p.Name}  ({p.Vendor})");

        Assert.All(found, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public void Loads_a_real_plugin_and_processes_audio_through_the_bridge()
    {
        var found = ScanAll();
        Assert.SkipWhen(found.Length == 0, "No LADSPA plugins on the search path — install some to run this.");

        // Prefer a tremolo (a clean, obviously-measurable amplitude effect); else the first plugin
        // whose port shape we support for a stereo bus.
        var ordered = found.OrderByDescending(p => p.Name.Contains("tremolo", StringComparison.OrdinalIgnoreCase));
        IHostedPlugin? plugin = null;
        foreach (var desc in ordered)
        {
            try { plugin = _format.Load(desc, Config); break; }
            catch (NotSupportedException) { /* odd port count for a stereo bus — try the next */ }
        }
        Assert.SkipWhen(plugin == null, "No LADSPA plugin with a stereo-compatible port shape was found.");

        using var effect = new HostedEffect(plugin!, Config);
        _out.WriteLine($"Loaded: {plugin!.Descriptor.Name}");
        _out.WriteLine($"Parameters ({plugin.Parameters.Count}):");
        foreach (var par in plugin.Parameters)
            _out.WriteLine($"  [{par.Index}] {par.Name}  [{par.MinValue:0.###}..{par.MaxValue:0.###}] def {par.DefaultValue:0.###}");

        const double amp = 0.4;
        var inputRms = amp / Math.Sqrt(2);   // RMS of a full-scale-amp sine

        PluginParameter? Find(params string[] names) => plugin.Parameters.FirstOrDefault(p =>
            names.Any(n => p.Name.Contains(n, StringComparison.OrdinalIgnoreCase)));

        // Render a 1 kHz sine through the effect and return the output RMS. `setup` configures params.
        double RenderRms(Action setup)
        {
            setup();
            effect.Reset();
            double sumSq = 0;
            long n = 0;
            var phase = 0.0;
            var buf = new float[Block * 2];
            for (var b = 0; b < 16; b++)
            {
                for (var i = 0; i < Block; i++)
                {
                    var s = (float)(amp * Math.Sin(phase));
                    phase += 2 * Math.PI * 1000.0 / Rate;
                    buf[2 * i] = s;
                    buf[2 * i + 1] = s;
                }
                effect.Process(buf);
                foreach (var v in buf)
                {
                    Assert.True(float.IsFinite(v), "Plugin produced a non-finite sample.");
                    sumSq += (double)v * v;
                    n++;
                }
            }
            return Math.Sqrt(sumSq / n);
        }

        var depth = Find("depth", "amount", "width");
        var rate = Find("freq", "rate", "speed");

        // Transparent baseline: every param at its default (for tremolo: depth 0 / freq 0 → bypass).
        var rmsFlat = RenderRms(() =>
        {
            foreach (var p in plugin.Parameters) plugin.SetParameter(p.Index, p.Normalize(p.DefaultValue));
        });

        _out.WriteLine($"output RMS: flat={rmsFlat:F5} ({Db(rmsFlat):F2} dBFS), input={inputRms:F5}");

        Assert.True(rmsFlat > 0, "Expected non-silent output from the plugin.");

        // A modulation effect (depth + rate controls): drive both and the average level must drop —
        // proof the parameter path and the native run loop are live, end to end.
        if (depth != null && rate != null)
        {
            var rmsMod = RenderRms(() =>
            {
                plugin.SetParameter(depth.Index, 1.0);    // full depth
                plugin.SetParameter(rate.Index, 0.4);     // a moderate LFO rate
            });
            _out.WriteLine($"output RMS: modulated={rmsMod:F5} ({Db(rmsMod):F2} dBFS)");

            // At default the plugin is transparent, so the baseline tracks the input level.
            Assert.True(Math.Abs(rmsFlat - inputRms) < 0.01,
                $"Bypassed tremolo should pass the sine through (flat={rmsFlat:F5}, input={inputRms:F5}).");
            // Full-depth tremolo multiplies by a 0..1 LFO envelope, measurably lowering average RMS.
            Assert.True(rmsMod < rmsFlat * 0.9,
                $"Full-depth modulation should drop the level (flat={rmsFlat:F5}, modulated={rmsMod:F5}).");
        }
    }

    private static double Db(double rms) => rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;
}
