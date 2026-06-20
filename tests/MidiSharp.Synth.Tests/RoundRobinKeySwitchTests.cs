using System;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Synth-level behavior of SFZ round-robin and keyswitch. Each test builds a tiny
/// in-memory <see cref="IRBank"/> with two zones on the same key whose samples are
/// distinguishable (one loud, one silent), drives the <see cref="Synthesizer"/>,
/// and checks both the active-voice count and the rendered output to confirm the
/// right zone fired.
/// </summary>
public sealed class RoundRobinKeySwitchTests
{
    private const int Rate = 44100;

    [Fact]
    public void WithoutRoundRobin_bothOverlappingZonesSound()
    {
        // Control: two plain zones on key 60 both match → two voices. This is the
        // baseline that round-robin/keyswitch must narrow to one.
        var bank = MakeBank(Zone(0), Zone(1));
        var synth = NewSynth(bank);

        synth.NoteOn(0, 60, 100);
        Assert.Equal(2, synth.ActiveVoiceCount);
    }

    [Fact]
    public void RoundRobin_rotatesOneZonePerNoteOn()
    {
        var bank = MakeBank(
            Zone(sampleId: 0, rr: new RoundRobin(0, 2)),   // loud, position 0
            Zone(sampleId: 1, rr: new RoundRobin(1, 2)));  // silent, position 1
        var synth = NewSynth(bank);

        synth.NoteOn(0, 60, 100);
        Assert.Equal(1, synth.ActiveVoiceCount);           // exactly one variant, not both
        Assert.True(RenderPeak(synth) > 0.1f);             // position 0 → loud sample

        synth.NoteOn(0, 60, 100);                          // retrigger → next in sequence
        Assert.Equal(1, synth.ActiveVoiceCount);
        Assert.True(RenderPeak(synth) < 0.01f);            // position 1 → silent sample

        synth.NoteOn(0, 60, 100);                          // wraps back to position 0
        Assert.True(RenderPeak(synth) > 0.1f);
    }

    [Fact]
    public void KeySwitch_pressingSwitchKeySoundsNoNote()
    {
        var bank = MakeBank(
            Zone(sampleId: 0, ks: new KeySwitch(24, 25, SelectingKey: 24, Default: 24)),
            Zone(sampleId: 1, ks: new KeySwitch(24, 25, SelectingKey: 25, Default: 24)));
        var synth = NewSynth(bank);

        synth.NoteOn(0, 60, 100);                          // default selection (24) → articulation A
        var afterNote = synth.ActiveVoiceCount;
        Assert.Equal(1, afterNote);

        synth.NoteOn(0, 25, 100);                          // a switch key — selects, sounds nothing
        Assert.Equal(afterNote, synth.ActiveVoiceCount);   // no new voice from the switch press
    }

    [Fact]
    public void KeySwitch_selectsArticulationByLastPressedSwitch()
    {
        var bank = MakeBank(
            Zone(sampleId: 0, ks: new KeySwitch(24, 25, SelectingKey: 24, Default: 24)),  // loud
            Zone(sampleId: 1, ks: new KeySwitch(24, 25, SelectingKey: 25, Default: 24))); // silent
        var synth = NewSynth(bank);

        // Before any switch: default 24 → articulation A (loud).
        synth.NoteOn(0, 60, 100);
        Assert.Equal(1, synth.ActiveVoiceCount);
        Assert.True(RenderPeak(synth) > 0.1f);

        // Select switch 25, then play: articulation B (silent).
        synth.NoteOn(0, 25, 100);
        synth.NoteOn(0, 60, 100);                          // retrigger kills A, starts B
        Assert.Equal(1, synth.ActiveVoiceCount);
        Assert.True(RenderPeak(synth) < 0.01f);

        // Switch back to 24 → A (loud) again.
        synth.NoteOn(0, 24, 100);
        synth.NoteOn(0, 60, 100);
        Assert.True(RenderPeak(synth) > 0.1f);
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

    private static PatchZone Zone(int sampleId, RoundRobin? rr = null, KeySwitch? ks = null) => new()
    {
        Keys = new KeyRange(60, 60),
        Velocities = new VelocityRange(0, 127),
        Sample = new SampleRef { SampleId = sampleId, OverridingRootKey = 60 },
        RoundRobin = rr,
        KeySwitch = ks,
    };

    private static IRBank MakeBank(params PatchZone[] zones)
    {
        // Sample 0 is a loud constant; sample 1 is silence — so the rendered peak
        // tells which zone fired.
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
