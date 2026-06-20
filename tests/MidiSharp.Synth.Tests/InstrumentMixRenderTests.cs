using System;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Measured proofs of the Tier-1 per-instrument mixer: an untouched mixer is bit-identical to the
/// pre-mixer engine, a gain trim scales by the right dB, mute silences, solo isolates, a pan offset
/// re-images, and a send adds wet energy. Built on a tiny synthetic bank (a constant sample per
/// program) so RMS is stable and isolation is exact.
/// </summary>
public sealed class InstrumentMixRenderTests
{
    private const int Rate = 44100;

    [Fact]
    public void Untouched_mixer_is_bit_identical()
    {
        var baseline = RenderProgram0(mix: null);
        // Touch the mixer (create an entry) but leave every field at its no-op default.
        var touched = RenderProgram0(synth => synth.GetInstrumentMix(0, 0));
        Assert.Equal(baseline.left, touched.left);     // exact sample-for-sample equality
        Assert.Equal(baseline.right, touched.right);
    }

    [Fact]
    public void Track_keyed_mix_trims_by_part_for_the_same_program()
    {
        // A note carrying a track index mixes by (TrackMixBank, TrackPart(track, channel)), not by its
        // program — so the mixer groups by part. Trimming track 5's part (channel 0) halves its level
        // even though it plays program 0.
        var flat = Rms(RenderTrackNote(5, null).left);
        var cut = Rms(RenderTrackNote(5, s => s.GetInstrumentMix(Synthesizer.TrackMixBank, Synthesizer.TrackPart(5, 0)).GainDb = -6.0206).left);
        Assert.True(Math.Abs(cut / flat - 0.5) < 0.02, $"track-5 -6 dB trim should halve RMS (ratio {cut / flat:F4})");
    }

    [Fact]
    public void Two_parts_on_the_same_program_mix_independently()
    {
        // Program 0 played by two different parts (track 3 / channel 0, and track 7 / channel 1).
        // Soloing the first silences the second even though they share the program — proof the mix
        // identity is the part, not the sound.
        var only3 = RenderTwoTrackNotes(soloTrack: 3, includeTrack7: false);
        var soloed = RenderTwoTrackNotes(soloTrack: 3, includeTrack7: true);
        Assert.Equal(only3.left, soloed.left);    // the other part contributes nothing when track 3 is soloed
        Assert.Equal(only3.right, soloed.right);
    }

    private static (float[] left, float[] right) RenderTrackNote(int track, Action<Synthesizer>? setup)
    {
        var synth = new Synthesizer();
        synth.LoadSoundFont(MakeBank(1));
        setup?.Invoke(synth);
        synth.NoteOn(0, 60, 120, track);
        return Render(synth, 40);
    }

    private static (float[] left, float[] right) RenderTwoTrackNotes(int soloTrack, bool includeTrack7)
    {
        var synth = new Synthesizer();
        synth.LoadSoundFont(MakeBank(1));
        synth.GetInstrumentMix(Synthesizer.TrackMixBank, Synthesizer.TrackPart(soloTrack, 0)).Solo = true;
        synth.NoteOn(0, 60, 120, soloTrack);
        if (includeTrack7) synth.NoteOn(1, 64, 120, 7);
        return Render(synth, 40);
    }

    [Fact]
    public void Gain_trim_plus_6dB_doubles_rms()
    {
        var flat = Rms(RenderProgram0(mix: null).left);
        var boosted = Rms(RenderProgram0(s => s.GetInstrumentMix(0, 0).GainDb = 6.0206).left);
        Assert.True(Math.Abs(boosted / flat - 2.0) < 0.02, $"+6 dB should ~double RMS (ratio {boosted / flat:F4})");
    }

    [Fact]
    public void Gain_trim_minus_6dB_halves_rms()
    {
        var flat = Rms(RenderProgram0(mix: null).left);
        var cut = Rms(RenderProgram0(s => s.GetInstrumentMix(0, 0).GainDb = -6.0206).left);
        Assert.True(Math.Abs(cut / flat - 0.5) < 0.02, $"-6 dB should ~halve RMS (ratio {cut / flat:F4})");
    }

    [Fact]
    public void Mute_silences_the_instrument()
    {
        var muted = Rms(RenderProgram0(s => s.GetInstrumentMix(0, 0).Mute = true).left);
        Assert.True(muted < 1e-6, $"mute should be silent (rms {muted:E3})");
    }

    [Fact]
    public void Solo_silences_others_and_leaves_the_soloed_part_bit_identical()
    {
        // Program 0 alone (the part we will solo) — the reference.
        var solo0Reference = RenderTwoPrograms(soloProgram0: false, muteAll: false, onlyProgram0: true);

        // Both parts, with program 0 soloed: program 1 must vanish and program 0 must be untouched.
        var soloed = RenderTwoPrograms(soloProgram0: true, muteAll: false, onlyProgram0: false);

        Assert.Equal(solo0Reference.left, soloed.left);    // soloed part is bit-identical to itself-alone
        Assert.Equal(solo0Reference.right, soloed.right);
    }

    [Fact]
    public void Pan_offset_hard_right_empties_the_left()
    {
        var neutral = RenderProgram0(mix: null);
        var hardRight = RenderProgram0(s => s.GetInstrumentMix(0, 0).Pan = 1.0);
        Assert.True(Rms(neutral.left) > 1e-3, "sanity: neutral pan has left signal");
        Assert.True(Rms(hardRight.left) < 1e-6, $"hard-right pan should empty the left (rms {Rms(hardRight.left):E3})");
        Assert.True(Rms(hardRight.right) > Rms(neutral.right), "hard-right pan should keep/raise the right");
    }

    [Fact]
    public void Reverb_send_adds_wet_energy()
    {
        // A short note then a tail; the reverb send should leave audible energy after the note ends.
        var dry = TotalEnergy(RenderProgram0(mix: null, blocks: 80));
        var wet = TotalEnergy(RenderProgram0(s => s.GetInstrumentMix(0, 0).ReverbSend = 1.0, blocks: 80));
        Assert.True(wet > dry * 1.01, $"reverb send should add wet energy (wet {wet:F3} vs dry {dry:F3})");
    }

    // ── render helpers ──

    private static (float[] left, float[] right) RenderProgram0(Action<Synthesizer>? mix, int blocks = 50)
    {
        var synth = new Synthesizer();
        synth.LoadSoundFont(MakeBank(2));
        mix?.Invoke(synth);
        synth.NoteOn(0, 60, 120);
        return Render(synth, blocks);
    }

    // Plays program 0 on channel 0 and program 1 on channel 1, with optional solo/mute/isolation.
    private static (float[] left, float[] right) RenderTwoPrograms(bool soloProgram0, bool muteAll, bool onlyProgram0)
    {
        var synth = new Synthesizer();
        synth.LoadSoundFont(MakeBank(2));
        if (soloProgram0) synth.GetInstrumentMix(0, 0).Solo = true;

        synth.NoteOn(0, 60, 120);                  // program 0 on channel 0
        if (!onlyProgram0)
        {
            synth.ProgramChange(1, 1);
            synth.NoteOn(1, 64, 120);              // program 1 on channel 1
        }
        return Render(synth, 50);
    }

    private static (float[] left, float[] right) Render(Synthesizer synth, int blocks)
    {
        const int n = 128;
        var left = new float[blocks * n];
        var right = new float[blocks * n];
        for (var b = 0; b < blocks; b++)
        {
            var l = new float[n];
            var r = new float[n];
            synth.Generate(l, r);
            Array.Copy(l, 0, left, b * n, n);
            Array.Copy(r, 0, right, b * n, n);
        }
        return (left, right);
    }

    private static IRBank MakeBank(int programs)
    {
        // One mono constant-0.5 sample, shared by every program; a flat sustained envelope.
        var data = new[] { Constant(0.5f, 16000) };
        var meta = new[] { new SampleMetadata { SampleRate = Rate, Channels = 1, LengthFrames = 16000, RootKey = 60 } };
        var patches = new Patch[programs];
        for (var p = 0; p < programs; p++)
        {
            patches[p] = new Patch
            {
                Bank = 0,
                Program = p,
                Zones =
                [
                    new PatchZone
                    {
                        Keys = new KeyRange(0, 127),
                        Velocities = new VelocityRange(0, 127),
                        Sample = new SampleRef { SampleId = 0, OverridingRootKey = 60 },
                        VolumeEnvelope = new EnvelopeSettings
                        {
                            AttackSeconds = 0, DecaySeconds = 0, SustainLevel = 1.0, ReleaseSeconds = 0.05,
                        },
                    }
                ],
            };
        }
        return new IRBank { Patches = patches, Samples = new PreDecodedFloatSampleSource(data, meta) };
    }

    private static float[] Constant(float v, int n)
    {
        var a = new float[n];
        Array.Fill(a, v);
        return a;
    }

    private static double Rms(float[] x)
    {
        double sum = 0;
        for (var i = 0; i < x.Length; i++) sum += (double)x[i] * x[i];
        return Math.Sqrt(sum / x.Length);
    }

    private static double TotalEnergy((float[] left, float[] right) s)
    {
        double sum = 0;
        for (var i = 0; i < s.left.Length; i++) sum += (double)s.left[i] * s.left[i] + (double)s.right[i] * s.right[i];
        return sum;
    }
}
