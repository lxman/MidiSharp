using System;
using System.Linq;
using MidiSharp.Hosting.Clap;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Phase-4 live gate: host a real CLAP instrument and drive it with note events. Uses the monophonic
/// sine fixture (midisharp.test.synth) so pitch, onset timing, and gating are exactly measurable; a
/// composition test also runs the instrument's output through a hosted effect insert. Self-skips when the
/// fixture isn't installed.
/// </summary>
public sealed class ClapInstrumentTests
{
    private const int Rate = 48000;
    private const int Block = 512;
    private static readonly AudioConfig Config = new(Rate, Block, ChannelCount: 2);

    private readonly ClapFormat _format = new();
    private readonly ITestOutputHelper _out;

    public ClapInstrumentTests(ITestOutputHelper output) => _out = output;

    private IHostedPlugin? TryLoad(string id)
    {
        PluginDescriptor? d = _format.Scan(_format.DefaultSearchPaths).FirstOrDefault(p => p.Id == id);
        return d == null ? null : _format.Load(d, Config);
    }

    // Render `blocks` consecutive blocks (events queued once, before the first) and return channel L.
    private static float[] Render(HostedInstrument inst, int blocks, Action<HostedInstrument>? queue = null)
    {
        queue?.Invoke(inst);
        var l = new float[blocks * Block];
        var buf = new float[Block * 2];
        for (var b = 0; b < blocks; b++)
        {
            Array.Clear(buf);
            inst.Render(buf);
            for (var i = 0; i < Block; i++) l[b * Block + i] = buf[2 * i];
        }
        return l;
    }

    private static double Rms(ReadOnlySpan<float> x)
    {
        double s = 0;
        foreach (float v in x) s += (double)v * v;
        return Math.Sqrt(s / x.Length);
    }

    private static double FreqHz(float[] x)
    {
        var crossings = 0;
        for (var i = 1; i < x.Length; i++)
            if ((x[i - 1] < 0f && x[i] >= 0f) || (x[i - 1] >= 0f && x[i] < 0f)) crossings++;
        return crossings * (double)Rate / (2.0 * x.Length);
    }

    [Fact]
    public void Loads_a_clap_instrument()
    {
        PluginDescriptor? d = _format.Scan(_format.DefaultSearchPaths).FirstOrDefault(p => p.Id == "midisharp.test.synth");
        Assert.SkipWhen(d == null, "synth fixture not installed.");
        Assert.True(d!.IsInstrument);
        using IHostedPlugin plugin = _format.Load(d, Config);
        Assert.Equal("MidiSharp Test Synth", plugin.Descriptor.Name);
    }

    [Fact]
    public void Silent_with_no_notes_then_sounds_at_the_right_pitch()
    {
        IHostedPlugin? plugin = TryLoad("midisharp.test.synth");
        Assert.SkipWhen(plugin == null, "synth fixture not installed.");
        using var inst = new HostedInstrument(plugin!, Config);

        float[] silent = Render(inst, 4);
        Assert.True(Rms(silent) < 1e-6, $"no note → silence (rms {Rms(silent):E2})");

        // A4 (key 69) = 440 Hz, sustained across 16 blocks for a stable pitch estimate.
        float[] tone = Render(inst, 16, i => i.NoteOn(0, channel: 0, key: 69, velocity: 100));
        double f = FreqHz(tone);
        _out.WriteLine($"note A4 → rms {Rms(tone):F4}, measured {f:F1} Hz (want 440)");
        Assert.True(Rms(tone) > 0.1, "note-on should produce sound.");
        Assert.True(Math.Abs(f - 440.0) < 10.0, $"A4 should sound at ~440 Hz (measured {f:F1}).");
    }

    [Fact]
    public void Note_onset_is_sample_accurate()
    {
        IHostedPlugin? plugin = TryLoad("midisharp.test.synth");
        Assert.SkipWhen(plugin == null, "synth fixture not installed.");
        using var inst = new HostedInstrument(plugin!, Config);

        const int onset = 256;
        float[] l = Render(inst, 1, i => i.NoteOn(onset, 0, 69, 100));

        // Strictly silent before the onset; energy after it → the note starts exactly at `onset`.
        for (var i = 0; i < onset; i++) Assert.Equal(0f, l[i]);
        Assert.True(Rms(l.AsSpan(onset)) > 0.1, "note should sound from its onset sample onward.");
        _out.WriteLine($"onset: last silent sample {onset - 1}, energy from {onset}");
    }

    [Fact]
    public void Note_off_silences_the_instrument()
    {
        IHostedPlugin? plugin = TryLoad("midisharp.test.synth");
        Assert.SkipWhen(plugin == null, "synth fixture not installed.");
        using var inst = new HostedInstrument(plugin!, Config);

        float[] on = Render(inst, 2, i => i.NoteOn(0, 0, 69, 100));
        Assert.True(Rms(on) > 0.1, "note should be sounding.");
        float[] off = Render(inst, 2, i => i.NoteOff(0, 0, 69));
        Assert.True(Rms(off) < 1e-6, $"after note-off → silence (rms {Rms(off):E2})");
    }

    // A specific real third-party instrument (DISTRHO Nekobi, a TB-303-style synth) — proof the host
    // drives real instruments, not just our fixture. Targeted by id (loading arbitrary plugins in-process
    // can segfault — Phase 8).
    [Fact]
    public void Loads_a_real_clap_instrument_and_a_note_produces_sound()
    {
        const string id = "studio.kx.distrho.Nekobi";
        IHostedPlugin? plugin = TryLoad(id);
        Assert.SkipWhen(plugin == null, $"{id} not installed.");
        using var inst = new HostedInstrument(plugin!, Config);

        float[] l = Render(inst, 32, i => i.NoteOn(0, 0, 45, 110));   // hold an A2 a while
        foreach (float v in l) Assert.True(float.IsFinite(v), "Nekobi produced a non-finite sample.");
        _out.WriteLine($"Nekobi {plugin!.Descriptor.Name}: rms {Rms(l):F4}, {plugin.Parameters.Count} params");
        Assert.True(Rms(l) > 1e-4, "a held note should produce audible output from Nekobi.");
    }

    [Fact]
    public void Instrument_output_runs_through_an_effect_insert()
    {
        IHostedPlugin? synth = TryLoad("midisharp.test.synth");
        IHostedPlugin? gain = TryLoad("midisharp.test.gain");
        Assert.SkipWhen(synth == null || gain == null, "synth/gain fixtures not installed.");
        using var inst = new HostedInstrument(synth!, Config);
        using var fx = new HostedEffect(gain!, Config);
        fx.Plugin.SetParameter(0, 0.25);   // ×0.5

        // Render the instrument, then pass the same block through the gain insert (the rack path).
        var buf = new float[Block * 2];
        inst.NoteOn(0, 0, 69, 100);
        inst.Render(buf);
        double dry = Rms(buf);
        fx.Process(buf);
        double wet = Rms(buf);
        _out.WriteLine($"instrument→insert: dry rms {dry:F4}, wet rms {wet:F4} (×0.5 expected)");
        Assert.True(dry > 0.1, "instrument should produce sound.");
        Assert.True(Math.Abs(wet - dry * 0.5) < 0.01, $"the gain insert should halve the level (dry {dry:F4}, wet {wet:F4}).");
    }
}
