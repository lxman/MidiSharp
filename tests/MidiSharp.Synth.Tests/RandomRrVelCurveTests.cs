using System;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Synth-level behavior of SFZ random round-robin (lorand/hirand) and the
/// amp_velcurve_N velocity→gain table. Uses the same loud/silent-sample technique as
/// <see cref="RoundRobinKeySwitchTests"/>: the rendered peak reveals which zone fired
/// or how much a velocity curve scaled the note.
/// </summary>
public sealed class RandomRrVelCurveTests
{
    private const int Rate = 44100;

    [Fact]
    public void RandomRoundRobin_picksExactlyOneZonePerNote_byTheRoll()
    {
        // Two zones tiling [0,1): [0,0.5) is loud, [0.5,1) is silent. Each note rolls once
        // and exactly one zone sounds; across many notes both variants occur.
        IRBank bank = MakeBank(
            Zone(sampleId: 0, random: new RandomRange(0.0, 0.5)),   // loud
            Zone(sampleId: 1, random: new RandomRange(0.5, 1.0)));  // silent
        Synthesizer synth = NewSynth(bank);

        int loud = 0, silent = 0;
        for (var n = 0; n < 64; n++)
        {
            synth.NoteOn(0, 60, 100);
            Assert.Equal(1, synth.ActiveVoiceCount);     // never both, never none — ranges tile [0,1)
            if (RenderPeak(synth) > 0.1f) loud++; else silent++;
            synth.NoteOff(0, 60);
        }

        Assert.True(loud > 0, "expected some notes to roll into the loud zone");
        Assert.True(silent > 0, "expected some notes to roll into the silent zone");
    }

    [Fact]
    public void RandomRoundRobin_isReproducible_acrossSynthsWithSameSeed()
    {
        IRBank bank = MakeBank(
            Zone(sampleId: 0, random: new RandomRange(0.0, 0.5)),
            Zone(sampleId: 1, random: new RandomRange(0.5, 1.0)));

        bool[] Run()
        {
            Synthesizer synth = NewSynth(bank);
            var seq = new bool[32];
            for (var n = 0; n < seq.Length; n++)
            {
                synth.NoteOn(0, 60, 100);
                seq[n] = RenderPeak(synth) > 0.1f;
                synth.NoteOff(0, 60);
            }
            return seq;
        }

        Assert.Equal(Run(), Run());   // fixed RNG seed → identical selection sequence
    }

    [Fact]
    public void AmpVelCurve_scalesGainByTheTableEntryForTheNotesVelocity()
    {
        // Curve forces low gain at velocity 40 and full gain at 120.
        var curve = new double[128];
        curve[40] = 0.0;
        curve[120] = 1.0;
        for (var v = 0; v < 128; v++) curve[v] = v <= 40 ? 0.0 : v >= 120 ? 1.0 : (v - 40) / 80.0;

        IRBank bank = MakeBank(Zone(sampleId: 0, ampVelCurve: curve));   // loud base sample
        Synthesizer synth = NewSynth(bank);

        synth.NoteOn(0, 60, 40);
        Assert.True(RenderPeak(synth) < 0.01f, "velocity 40 maps to ~0 gain");
        synth.NoteOff(0, 60);

        synth.NoteOn(0, 60, 120);
        Assert.True(RenderPeak(synth) > 0.1f, "velocity 120 maps to full gain");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static Synthesizer NewSynth(IRBank bank)
    {
        var synth = new Synthesizer();
        synth.LoadSoundFont(bank);
        return synth;
    }

    private static float RenderPeak(Synthesizer synth, int frames = 64)
    {
        var l = new float[frames];
        var r = new float[frames];
        synth.Generate(l, r);
        float peak = 0;
        for (var i = 0; i < frames; i++) peak = Math.Max(peak, Math.Abs(l[i]));
        return peak;
    }

    private static PatchZone Zone(int sampleId, RandomRange? random = null, double[]? ampVelCurve = null) => new()
    {
        Keys = new KeyRange(60, 60),
        Velocities = new VelocityRange(0, 127),
        Sample = new SampleRef { SampleId = sampleId, OverridingRootKey = 60 },
        Random = random,
        AmpVelCurve = ampVelCurve,
    };

    private static IRBank MakeBank(params PatchZone[] zones)
    {
        var samples = new PreDecodedFloatSampleSource(
            [Constant(0.5f, 2000), Constant(0.0f, 2000)],
            [Meta(), Meta()]);
        return new IRBank
        {
            Patches = [new Patch { Bank = 0, Program = 0, Zones = zones }],
            Samples = samples,
        };
    }

    private static SampleMetadata Meta() => new()
    {
        SampleRate = Rate,
        Channels = 1,
        LengthFrames = 2000,
        RootKey = 60,
    };

    private static float[] Constant(float v, int n)
    {
        var a = new float[n];
        Array.Fill(a, v);
        return a;
    }
}
