using System;
using MidiSharp.SoundBank;
using MidiSharp.Synth;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Synth-level proof that the final tail singletons reach the audio path: SFZ sustain_cc reassignment,
/// ampeg_release_shape curvature, and per-region polyphony stealing.
/// </summary>
public sealed class TailSingletonRenderTests
{
    private const int Rate = 44100;

    // ── sustain_cc ──────────────────────────────────────────────────────

    [Fact]
    public void Sustain_cc_reassignment_makes_cc90_hold_notes()
    {
        // Reassigned bank: CC90 is the pedal. Hold it, release the key → the note keeps sounding.
        float held = LevelAfterPedalledNoteOff(sustainCc: 90, pedalCc: 90);
        // Default bank: CC90 is not the pedal, so the same sequence releases the note → it decays away.
        float notHeld = LevelAfterPedalledNoteOff(sustainCc: 64, pedalCc: 90);
        Assert.True(held > notHeld * 5.0,
            $"CC90 should hold only when reassigned as sustain (held {held}, notHeld {notHeld})");
    }

    private static float LevelAfterPedalledNoteOff(int sustainCc, int pedalCc)
    {
        var synth = new Synthesizer(Rate);
        synth.LoadSoundFont(MakeBank(releaseShape: 0, releaseSeconds: 0.01, sustainCc: sustainCc));
        synth.NoteOn(0, 60, 110);
        Render(synth, 200);                 // let it reach sustain
        synth.ControlChange(0, pedalCc, 127); // press the (maybe-) pedal
        synth.NoteOff(0, 60);
        Render(synth, 4000);                // well past the 10 ms release
        return PeakOver(synth, 500);
    }

    // ── ampeg_release_shape ─────────────────────────────────────────────

    [Fact]
    public void Release_shape_changes_release_trajectory()
    {
        // Mid-release, a concave shape (+6 → exponent 0.5) holds a higher level than a convex one
        // (−6 → exponent 2), proving the shape exponent is applied to the release ramp.
        float concave = ReleaseLevelMidway(shape: 6);
        float convex = ReleaseLevelMidway(shape: -6);
        Assert.True(concave > convex * 1.3,
            $"concave (+6) should sit above convex (−6) mid-release (concave {concave}, convex {convex})");
    }

    private static float ReleaseLevelMidway(double shape)
    {
        var synth = new Synthesizer(Rate);
        synth.LoadSoundFont(MakeBank(releaseShape: shape, releaseSeconds: 0.2, sustainCc: 64));
        synth.NoteOn(0, 60, 110);
        Render(synth, 200);          // reach sustain
        synth.NoteOff(0, 60);
        Render(synth, 2646);         // ~30% into the 0.2 s release window
        return PeakOver(synth, 200);
    }

    // ── polyphony (per-region voice cap) ────────────────────────────────

    [Fact]
    public void Region_polyphony_caps_simultaneous_voices()
    {
        var capped = new Synthesizer(Rate);
        capped.LoadSoundFont(MakeRangeBank(polyphony: 1));
        capped.NoteOn(0, 60, 100);
        capped.NoteOn(0, 62, 100);   // same region — steals the first
        Render(capped, 100);
        Assert.Equal(1, capped.ActiveVoiceCount);

        var open = new Synthesizer(Rate);
        open.LoadSoundFont(MakeRangeBank(polyphony: -1));
        open.NoteOn(0, 60, 100);
        open.NoteOn(0, 62, 100);
        Render(open, 100);
        Assert.Equal(2, open.ActiveVoiceCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static void Render(Synthesizer synth, int frames)
    {
        var l = new float[frames];
        var r = new float[frames];
        synth.Generate(l, r);
    }

    private static float PeakOver(Synthesizer synth, int frames)
    {
        var l = new float[frames];
        var r = new float[frames];
        synth.Generate(l, r);
        float peak = 0;
        for (int i = 0; i < frames; i++) peak = Math.Max(peak, Math.Abs(l[i]));
        return peak;
    }

    private static IRBank MakeBank(double releaseShape, double releaseSeconds, int sustainCc)
    {
        var zone = new PatchZone
        {
            Keys = new KeyRange(60, 60),
            Velocities = new VelocityRange(0, 127),
            Sample = new SampleRef { SampleId = 0, OverridingRootKey = 60 },
            VolumeEnvelope = new EnvelopeSettings
            {
                AttackSeconds = 0.001,
                SustainLevel = 1.0,
                ReleaseSeconds = releaseSeconds,
                ReleaseShape = releaseShape,
            },
        };
        return BankFrom(new[] { zone }, sustainCc);
    }

    private static IRBank MakeRangeBank(int polyphony)
    {
        var zone = new PatchZone
        {
            Keys = new KeyRange(48, 72),
            Velocities = new VelocityRange(0, 127),
            Sample = new SampleRef { SampleId = 0, OverridingRootKey = 60 },
            VolumeEnvelope = new EnvelopeSettings { AttackSeconds = 0.001, SustainLevel = 1.0, ReleaseSeconds = 0.1 },
            Polyphony = polyphony,
        };
        return BankFrom(new[] { zone }, sustainCc: 64);
    }

    private static IRBank BankFrom(PatchZone[] zones, int sustainCc)
    {
        var data = new[] { Constant(0.5f, 44100) };  // 1 s of looping-length constant tone
        var meta = new[] { new SampleMetadata { SampleRate = Rate, Channels = 1, LengthFrames = 44100, RootKey = 60 } };
        return new IRBank
        {
            Patches = new[] { new Patch { Bank = 0, Program = 0, Zones = zones } },
            Samples = new PreDecodedFloatSampleSource(data, meta),
            SustainCc = sustainCc,
        };
    }

    private static float[] Constant(float v, int n)
    {
        var a = new float[n];
        Array.Fill(a, v);
        return a;
    }
}
