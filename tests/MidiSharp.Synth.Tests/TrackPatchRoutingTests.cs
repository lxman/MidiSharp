using System;
using System.Collections.Generic;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Per-track instrument routing at the synth level. A note carrying a track index that the
/// track-patch map covers must resolve to the mapped (bank, program) instead of the channel's
/// program — letting two parts on the same channel sound different instruments. The bank holds
/// a loud patch at the channel address and a silent one at the synthetic track address, so the
/// rendered peak tells which patch the note actually used.
/// </summary>
public sealed class TrackPatchRoutingTests
{
    private const int Rate = 44100;
    private const int ChannelBank = 0, ChannelProgram = 0;     // loud sample
    private const int TrackBank = 777, TrackProgram = 5;       // silent sample (a stand-in reserved address)

    [Fact]
    public void NoteOn_withMappedTrack_usesOverridePatch()
    {
        var synth = NewSynth();
        synth.SetTrackPatchMap(new Dictionary<int, (int Bank, int Program)> { [11] = (TrackBank, TrackProgram) });

        synth.NoteOn(0, 60, 100, trackIndex: 11);              // track 11 → silent override patch
        Assert.Equal(1, synth.ActiveVoiceCount);
        Assert.True(RenderPeak(synth) < 0.01f);
    }

    [Fact]
    public void NoteOn_withUnmappedTrack_fallsBackToChannelPatch()
    {
        var synth = NewSynth();
        synth.SetTrackPatchMap(new Dictionary<int, (int Bank, int Program)> { [11] = (TrackBank, TrackProgram) });

        synth.NoteOn(0, 60, 100, trackIndex: 4);               // track 4 unmapped → loud channel patch
        Assert.True(RenderPeak(synth) > 0.1f);
    }

    [Fact]
    public void NoteOn_withoutTrackIndex_usesChannelPatch()
    {
        var synth = NewSynth();
        synth.SetTrackPatchMap(new Dictionary<int, (int Bank, int Program)> { [11] = (TrackBank, TrackProgram) });

        synth.NoteOn(0, 60, 100);                              // 3-arg overload → trackIndex -1 → channel patch
        Assert.True(RenderPeak(synth) > 0.1f);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static Synthesizer NewSynth()
    {
        var synth = new Synthesizer();
        synth.LoadSoundFont(MakeBank());
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

    private static PatchZone Zone(int sampleId) => new()
    {
        Keys = new KeyRange(60, 60),
        Velocities = new VelocityRange(0, 127),
        Sample = new SampleRef { SampleId = sampleId, OverridingRootKey = 60 },
    };

    private static IRBank MakeBank()
    {
        // Sample 0 is loud, sample 1 is silent. The channel patch uses the loud sample; the
        // synthetic track-override patch uses the silent one.
        var samples = new PreDecodedFloatSampleSource(
            [Constant(0.5f, 2000), Constant(0.0f, 2000)],
            [Meta(), Meta()]);

        return new IRBank
        {
            Patches =
            [
                new Patch { Bank = ChannelBank, Program = ChannelProgram, Zones = [Zone(0)] },
                new Patch { Bank = TrackBank, Program = TrackProgram, Zones = [Zone(1)] }
            ],
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
