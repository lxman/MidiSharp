using System;
using MidiSharp.SoundBank;
using MidiSharp.Synth;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Synth-level proof that the SFZ v2 generic LFO (lfoN_*) actually reaches the audio path, not just
/// the loader. A constant sample with a strong volume LFO must produce an output whose amplitude
/// oscillates; a no-LFO zone must render flat.
/// </summary>
public sealed class GenericLfoRenderTests
{
    private const int Rate = 44100;

    [Fact]
    public void Generic_volume_lfo_modulates_output_amplitude()
    {
        // 30 Hz sine LFO, +-12 dB on volume. The sample is constant, so the only amplitude movement
        // can come from the LFO (the envelope is held at full sustain).
        var lfo = new GenericLfo
        {
            FrequencyHz = 30,
            Stages = new[] { new LfoStage(1, 1.0, 1.0, 0.0) },   // sine
            Targets = new[] { new LfoTarget { Destination = LfoDestination.Volume, Depth = 12 } },
        };
        var (min, max) = RenderPeakWindow(WithLfo(lfo));
        Assert.True(max > min * 3.0, $"volume LFO should swing the output (max {max}, min {min})");
    }

    [Fact]
    public void No_lfo_zone_renders_flat()
    {
        var (min, max) = RenderPeakWindow(WithLfo(null));
        Assert.True(max <= min * 1.05, $"a held constant sample with no LFO should be flat (max {max}, min {min})");
    }

    // Renders ~6000 frames in 100-frame blocks and returns the min/max block peak after the attack.
    private static (float Min, float Max) RenderPeakWindow(PatchZone zone)
    {
        var synth = new Synthesizer(Rate);
        synth.LoadSoundFont(MakeBank(zone));
        synth.NoteOn(0, 60, 120);

        float min = float.MaxValue, max = 0;
        for (int blk = 0; blk < 60; blk++)
        {
            var l = new float[100];
            var r = new float[100];
            synth.Generate(l, r);
            float p = 0;
            for (int i = 0; i < 100; i++) p = Math.Max(p, Math.Abs(l[i]));
            if (blk < 5) continue;   // skip the envelope attack
            min = Math.Min(min, p);
            max = Math.Max(max, p);
        }
        return (min, max);
    }

    private static PatchZone WithLfo(GenericLfo? lfo) => new()
    {
        Keys = new KeyRange(60, 60),
        Velocities = new VelocityRange(0, 127),
        Sample = new SampleRef { SampleId = 0, OverridingRootKey = 60 },
        VolumeEnvelope = new EnvelopeSettings
        {
            AttackSeconds = 0.0, DecaySeconds = 0.0, SustainLevel = 1.0, ReleaseSeconds = 0.1,
        },
        Lfos = lfo is null ? null : new[] { lfo },
    };

    private static IRBank MakeBank(params PatchZone[] zones)
    {
        var samples = new PreDecodedFloatSampleSource(
            new[] { Constant(0.5f, 8000) }, new[] { Meta() });
        return new IRBank
        {
            Patches = new[] { new Patch { Bank = 0, Program = 0, Zones = zones } },
            Samples = samples,
        };
    }

    private static SampleMetadata Meta() => new()
    {
        SampleRate = Rate,
        Channels = 1,
        LengthFrames = 8000,
        RootKey = 60,
    };

    private static float[] Constant(float v, int n)
    {
        var a = new float[n];
        Array.Fill(a, v);
        return a;
    }
}
