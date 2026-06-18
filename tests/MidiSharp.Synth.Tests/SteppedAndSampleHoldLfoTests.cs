using System;
using System.Linq;
using MidiSharp.SoundBank;
using MidiSharp.Synth;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Synth-level proof that LFO waves 12 (random sample-and-hold) and 13 (stepped) reach the audio path as
/// real held/staircase shapes — they previously fell back to a (smooth) sine. Both drive volume so the
/// output amplitude follows the LFO; we sample specific frames and check the held-value behaviour.
/// </summary>
public sealed class SteppedAndSampleHoldLfoTests
{
    private const int Rate = 44100;
    private const double LfoHz = 5.0;                 // 0.2 s period
    private static int Period => (int)(Rate / LfoHz); // 8820 frames

    [Fact]
    public void Stepped_lfo_walks_its_step_table_as_held_levels()
    {
        // Two steps: +1 (loud) for the first half-period, -1 (quiet) for the second, held flat within each.
        var lfo = VolumeLfo(wave: 13, steps: new[] { 1.0, -1.0 });
        var amp = RenderAmplitude(lfo, Period * 2);

        double early0 = amp[Period / 8], late0 = amp[3 * Period / 8];     // two points inside step 0
        double early1 = amp[5 * Period / 8], late1 = amp[7 * Period / 8]; // two points inside step 1

        // Held: amplitude is flat within a step (a sine fallback would vary continuously across it).
        Assert.True(Math.Abs(early0 - late0) / early0 < 0.02, $"step 0 not held ({early0} vs {late0})");
        Assert.True(Math.Abs(early1 - late1) / early1 < 0.02, $"step 1 not held ({early1} vs {late1})");
        // The two steps are clearly different levels (+1 vs -1 through a 12 dB depth ≈ 15× ratio).
        Assert.True(early0 > early1 * 8.0, $"steps should differ greatly ({early0} vs {early1})");
    }

    [Fact]
    public void Sample_hold_is_held_random_and_deterministic()
    {
        var lfo = VolumeLfo(wave: 12, steps: null);

        // Deterministic: two independent renders are identical (S&H is hashed, not RNG-stream coupled).
        var a = RenderAmplitude(lfo, Period * 3);
        var b = RenderAmplitude(lfo, Period * 3);
        Assert.Equal(a, b);

        // Held within each half-period: S&H samples a new value twice per period and holds it.
        double e = a[Period / 8], l = a[3 * Period / 8];   // both inside the first half-period
        Assert.True(Math.Abs(e - l) / e < 0.02, $"first half-period not held ({e} vs {l})");

        // Random, not a fixed 2-level square: sample the centre of six successive half-periods and expect
        // several distinct held levels.
        var centres = Enumerable.Range(0, 6)
            .Select(h => Math.Round((double)a[(int)((h + 0.5) * Period / 2)], 4))
            .Distinct().Count();
        Assert.True(centres >= 3, $"expected varied S&H levels, got {centres} distinct");
    }

    // Renders n frames and returns per-frame |left| amplitude.
    private static float[] RenderAmplitude(GenericLfo lfo, int n)
    {
        var synth = new Synthesizer(Rate);
        synth.LoadSoundFont(MakeBank(ZoneWith(lfo)));
        synth.NoteOn(0, 60, 120);
        var l = new float[n];
        var r = new float[n];
        synth.Generate(l, r);
        for (int i = 0; i < n; i++) l[i] = Math.Abs(l[i]);
        return l;
    }

    private static GenericLfo VolumeLfo(int wave, double[]? steps) => new()
    {
        FrequencyHz = LfoHz,
        Stages = new[] { new LfoStage(wave, 1.0, 1.0, 0.0) { Steps = steps } },
        Targets = new[] { new LfoTarget { Destination = LfoDestination.Volume, Depth = 12.0 } },
    };

    private static PatchZone ZoneWith(GenericLfo lfo) => new()
    {
        Keys = new KeyRange(60, 60),
        Velocities = new VelocityRange(0, 127),
        Sample = new SampleRef { SampleId = 0, OverridingRootKey = 60 },
        // Flat envelope so amplitude is purely LFO-driven.
        VolumeEnvelope = new EnvelopeSettings { AttackSeconds = 0.0, SustainLevel = 1.0, ReleaseSeconds = 0.1 },
        Lfos = new[] { lfo },
    };

    private static IRBank MakeBank(PatchZone zone)
    {
        // Low constant amplitude so a +12 dB LFO peak doesn't clip.
        var samples = new PreDecodedFloatSampleSource(
            new[] { Constant(0.1f, Period * 4) },
            new[] { new SampleMetadata { SampleRate = Rate, Channels = 1, LengthFrames = Period * 4, RootKey = 60 } });
        return new IRBank
        {
            Patches = new[] { new Patch { Bank = 0, Program = 0, Zones = new[] { zone } } },
            Samples = samples,
        };
    }

    private static float[] Constant(float v, int n)
    {
        var a = new float[n];
        Array.Fill(a, v);
        return a;
    }
}
