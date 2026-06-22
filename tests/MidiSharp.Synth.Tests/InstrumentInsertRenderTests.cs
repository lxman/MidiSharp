using System;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Proves the Tier-2 per-instrument insert path: no inserts is bit-identical to the pre-Tier-2 engine
/// (Invariant 3), a pass-through insert round-trips a single instrument bit-identically, a gain insert
/// scales only its instrument, and an insert on one instrument leaves another untouched.
/// </summary>
public sealed class InstrumentInsertRenderTests
{
    private const int Rate = 44100;

    private sealed class PassthroughInsert : IInstrumentInsert
    {
        public void Process(Span<float> s) { }
    }

    private sealed class GainInsert : IInstrumentInsert
    {
        public float Gain;
        public void Process(Span<float> s) { for (var i = 0; i < s.Length; i++) s[i] *= Gain; }
    }

    [Fact]
    public void No_inserts_is_bit_identical()
    {
        (float[] left, float[] right) baseline = RenderProgram0(null);
        // Register then clear an insert — the snapshot is empty again, so the pre-Tier-2 path runs.
        (float[] left, float[] right) cleared = RenderProgram0(s => { s.SetInstrumentInsert(0, 0, new GainInsert { Gain = 0.5f }); s.ClearInstrumentInserts(); });
        Assert.Equal(baseline.left, cleared.left);
        Assert.Equal(baseline.right, cleared.right);
    }

    [Fact]
    public void Passthrough_insert_is_bit_identical_for_a_single_instrument()
    {
        (float[] left, float[] right) baseline = RenderProgram0(null);
        (float[] left, float[] right) passthrough = RenderProgram0(s => s.SetInstrumentInsert(0, 0, new PassthroughInsert()));
        Assert.Equal(baseline.left, passthrough.left);    // bus round-trip (clear→+=→interleave→+=) is exact
        Assert.Equal(baseline.right, passthrough.right);
    }

    [Fact]
    public void Gain_insert_scales_the_instrument()
    {
        double flat = Rms(RenderProgram0(null).left);
        double half = Rms(RenderProgram0(s => s.SetInstrumentInsert(0, 0, new GainInsert { Gain = 0.5f })).left);
        Assert.True(Math.Abs(half / flat - 0.5) < 0.001, $"×0.5 insert should halve RMS (ratio {half / flat:F4})");
    }

    [Fact]
    public void Insert_on_one_instrument_leaves_another_untouched()
    {
        // Program 1 alone — the reference for the part we are NOT inserting on.
        (float[] left, float[] right) prog1Only = RenderTwo(insertOnProg0: null, onlyProgram1: true);
        // Both parts, with a silencing (×0) insert on program 0: master must equal program 1 alone.
        (float[] left, float[] right) muted0 = RenderTwo(insertOnProg0: new GainInsert { Gain = 0f }, onlyProgram1: false);
        Assert.Equal(prog1Only.left, muted0.left);
        Assert.Equal(prog1Only.right, muted0.right);
    }

    // ── helpers ──

    private static (float[] left, float[] right) RenderProgram0(Action<Synthesizer>? setup, int blocks = 40)
    {
        var synth = new Synthesizer();
        synth.LoadSoundFont(MakeBank(2));
        setup?.Invoke(synth);
        synth.NoteOn(0, 60, 120);
        return Render(synth, blocks);
    }

    private static (float[] left, float[] right) RenderTwo(IInstrumentInsert? insertOnProg0, bool onlyProgram1)
    {
        var synth = new Synthesizer();
        synth.LoadSoundFont(MakeBank(2));
        if (insertOnProg0 != null) synth.SetInstrumentInsert(0, 0, insertOnProg0);
        if (!onlyProgram1) synth.NoteOn(0, 60, 120);     // program 0 on channel 0
        synth.ProgramChange(1, 1);
        synth.NoteOn(1, 64, 120);                        // program 1 on channel 1
        return Render(synth, 40);
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
        float[][] data = new[] { Constant(0.5f, 16000) };
        var meta = new[] { new SampleMetadata { SampleRate = Rate, Channels = 1, LengthFrames = 16000, RootKey = 60 } };
        var patches = new Patch[programs];
        for (var p = 0; p < programs; p++)
            patches[p] = new Patch
            {
                Bank = 0, Program = p,
                Zones =
                [
                    new PatchZone
                    {
                        Keys = new KeyRange(0, 127), Velocities = new VelocityRange(0, 127),
                        Sample = new SampleRef { SampleId = 0, OverridingRootKey = 60 },
                        VolumeEnvelope = new EnvelopeSettings { AttackSeconds = 0, DecaySeconds = 0, SustainLevel = 1.0, ReleaseSeconds = 0.05 },
                    }
                ],
            };
        return new IRBank { Patches = patches, Samples = new PreDecodedFloatSampleSource(data, meta) };
    }

    private static float[] Constant(float v, int n) { var a = new float[n]; Array.Fill(a, v); return a; }

    private static double Rms(float[] x)
    {
        double sum = 0;
        for (var i = 0; i < x.Length; i++) sum += (double)x[i] * x[i];
        return Math.Sqrt(sum / x.Length);
    }
}
