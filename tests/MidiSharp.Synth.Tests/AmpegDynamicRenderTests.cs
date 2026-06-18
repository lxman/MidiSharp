using System;
using MidiSharp.SoundBank;
using MidiSharp.Synth;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Synth-level proof that SFZ ampeg_dynamic reaches the audio path: the envelope's CC-modulated attack
/// is read from the LIVE controller at note-on, so a higher CC1 lengthens the attack (the note rises
/// more slowly), not just at the loader.
/// </summary>
public sealed class AmpegDynamicRenderTests
{
    private const int Rate = 44100;

    [Fact]
    public void Ampeg_dynamic_attack_responds_to_live_cc()
    {
        // base attack 5 ms; CC1 adds up to +0.5 s. So CC1=0 → ~5 ms attack, CC1=127 → ~505 ms.
        var env = new EnvelopeSettings
        {
            AttackSeconds = 0.005,
            DecaySeconds = 0.0,
            SustainLevel = 1.0,
            ReleaseSeconds = 0.1,
            Dynamic = true,
            CcMods = new[] { new EnvCcMod(EnvStage.Attack, 1, 0.5, 0) },
        };

        float fast = PeakOver45ms(env, cc1: 0);     // ~5 ms attack → near full within the window
        float slow = PeakOver45ms(env, cc1: 127);   // ~505 ms attack → barely begun within the window
        Assert.True(fast > slow * 2.0, $"live CC1 should slow the attack (fast {fast}, slow {slow})");
    }

    private static float PeakOver45ms(EnvelopeSettings env, int cc1)
    {
        var synth = new Synthesizer(Rate);
        synth.LoadSoundFont(MakeBank(env));
        synth.ControlChange(0, 1, cc1);
        synth.NoteOn(0, 60, 120);

        float peak = 0;
        for (int blk = 0; blk < 20; blk++)   // 20 × 100 = ~45 ms
        {
            var l = new float[100];
            var r = new float[100];
            synth.Generate(l, r);
            for (int i = 0; i < 100; i++) peak = Math.Max(peak, Math.Abs(l[i]));
        }
        return peak;
    }

    private static IRBank MakeBank(EnvelopeSettings env)
    {
        var zone = new PatchZone
        {
            Keys = new KeyRange(60, 60),
            Velocities = new VelocityRange(0, 127),
            Sample = new SampleRef { SampleId = 0, OverridingRootKey = 60 },
            VolumeEnvelope = env,
        };
        var data = new[] { Constant(0.5f, 4000) };
        var meta = new[] { new SampleMetadata { SampleRate = Rate, Channels = 1, LengthFrames = 4000, RootKey = 60 } };
        return new IRBank
        {
            Patches = new[] { new Patch { Bank = 0, Program = 0, Zones = new[] { zone } } },
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
