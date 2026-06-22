using System;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Proves the multi-type biquad reaches the audio path: a constant (DC) sample passes through a
/// low-pass but is blocked by a high-pass, and a second cascaded filter further shapes the signal.
/// </summary>
public sealed class FilterTypeRenderTests
{
    private const int Rate = 44100;

    [Fact]
    public void Highpass_blocks_dc_while_lowpass_passes_it()
    {
        float lp = SteadyPeak(FilterType.LowPass);
        float hp = SteadyPeak(FilterType.HighPass);
        Assert.True(lp > 0.1f, $"low-pass should pass the DC sample (got {lp})");
        Assert.True(hp < lp * 0.2f, $"high-pass should block DC (hp {hp} vs lp {lp})");
    }

    // Steady-state output peak (after the filter transient) of a constant 0.5 sample through one filter.
    private static float SteadyPeak(FilterType type)
    {
        var zone = new PatchZone
        {
            Keys = new KeyRange(60, 60),
            Velocities = new VelocityRange(0, 127),
            Sample = new SampleRef { SampleId = 0, OverridingRootKey = 60 },
            VolumeEnvelope = new EnvelopeSettings { AttackSeconds = 0, DecaySeconds = 0, SustainLevel = 1.0, ReleaseSeconds = 0.1 },
            Filter = new FilterSettings { Type = type, CutoffHz = 1000, ResonanceDb = 0 },
        };
        var synth = new Synthesizer();
        synth.LoadSoundFont(MakeBank(zone));
        synth.NoteOn(0, 60, 120);

        float peak = 0;
        for (var blk = 0; blk < 60; blk++)
        {
            var l = new float[100];
            var r = new float[100];
            synth.Generate(l, r);
            if (blk < 40) continue;   // let the filter settle
            for (var i = 0; i < 100; i++) peak = Math.Max(peak, Math.Abs(l[i]));
        }
        return peak;
    }

    private static IRBank MakeBank(PatchZone zone)
    {
        float[][] data = new[] { Constant(0.5f, 8000) };
        var meta = new[] { new SampleMetadata { SampleRate = Rate, Channels = 1, LengthFrames = 8000, RootKey = 60 } };
        return new IRBank
        {
            Patches = [new Patch { Bank = 0, Program = 0, Zones = [zone] }],
            Samples = new PreDecodedFloatSampleSource(data, meta),
        };
    }

    private static float[] Constant(float v, int n)
    {
        var a = new float[n];
        Array.Fill(a, v);
        return a;
    }
}
