using System;
using Xunit;

namespace MidiSharp.Dsp.Tests;

/// <summary>
/// Measured proofs that the master-EQ processors do what they claim on a real interleaved-stereo
/// block: an empty EQ is a pass-through, a low-shelf lifts a low tone, a high-shelf cuts a high tone,
/// a peaking band boosts only near its centre, and a gain processor scales by the right dB.
/// </summary>
public sealed class ParametricEqTests
{
    private const int Rate = 48000;

    [Fact]
    public void Empty_eq_is_bit_identical_passthrough()
    {
        var eq = new ParametricEq(Rate);   // no bands set
        var buf = Tone(440, 0.3f, 2048);
        var copy = (float[])buf.Clone();
        eq.Process(buf);
        Assert.Equal(copy, buf);   // exact: an empty cascade must not touch a single sample
    }

    [Fact]
    public void Low_shelf_boost_raises_a_low_tone()
    {
        var before = Rms(Tone(80, 0.3f, 8192));
        var eq = new ParametricEq(Rate);
        eq.SetBands([EqBandSpec.LowShelf(200, gainDb: 9.0)]);
        var after = Rms(ProcessSettled(eq, Tone(80, 0.3f, 8192)));
        // +9 dB shelf below 200 Hz ⇒ an 80 Hz tone should be ~2–3× louder.
        Assert.True(after > before * 2.0, $"low-shelf boost: after {after:F4} vs before {before:F4}");
    }

    [Fact]
    public void High_shelf_cut_attenuates_a_high_tone()
    {
        var before = Rms(Tone(9000, 0.3f, 8192));
        var eq = new ParametricEq(Rate);
        eq.SetBands([EqBandSpec.HighShelf(4000, gainDb: -12.0)]);
        var after = Rms(ProcessSettled(eq, Tone(9000, 0.3f, 8192)));
        Assert.True(after < before * 0.5, $"high-shelf cut: after {after:F4} vs before {before:F4}");
    }

    [Fact]
    public void Peaking_band_boosts_on_centre_but_not_far_away()
    {
        var eq = new ParametricEq(Rate);
        eq.SetBands([EqBandSpec.Peak(1000, q: 2.0, gainDb: 12.0)]);

        var onCentre = Rms(ProcessSettled(eq, Tone(1000, 0.3f, 8192)))
                       / Rms(Tone(1000, 0.3f, 8192));
        eq.Reset();
        var farAway = Rms(ProcessSettled(eq, Tone(80, 0.3f, 8192)))
                      / Rms(Tone(80, 0.3f, 8192));

        Assert.True(onCentre > 2.0, $"on-centre should be boosted ~4× (got {onCentre:F3})");
        Assert.True(Math.Abs(farAway - 1.0) < 0.15, $"80 Hz should be ~unchanged (got {farAway:F3})");
    }

    [Fact]
    public void Gain_processor_scales_by_dB()
    {
        var gain = new GainProcessor { GainDb = 6.0206 };   // ×2.0
        var buf = Tone(440, 0.25f, 1024);
        var before = Rms(buf);
        gain.Process(buf);
        var after = Rms(buf);
        Assert.True(Math.Abs(after / before - 2.0) < 0.01, $"+6 dB should double RMS (ratio {after / before:F4})");
    }

    [Fact]
    public void Zero_dB_gain_is_passthrough()
    {
        var gain = new GainProcessor { GainDb = 0 };
        var buf = Tone(440, 0.25f, 512);
        var copy = (float[])buf.Clone();
        gain.Process(buf);
        Assert.Equal(copy, buf);
    }

    // ── helpers ──

    // Runs the processor over the block twice; returns the second pass so filter transients have settled.
    private static float[] ProcessSettled(IAudioProcessor p, float[] block)
    {
        p.Process(block);
        p.Process(block);
        return block;
    }

    private static float[] Tone(double freqHz, float amp, int frames)
    {
        var buf = new float[frames * 2];
        var step = 2.0 * Math.PI * freqHz / Rate;
        for (var f = 0; f < frames; f++)
        {
            var s = (float)(amp * Math.Sin(step * f));
            buf[f * 2] = s;
            buf[f * 2 + 1] = s;
        }
        return buf;
    }

    private static double Rms(float[] interleaved)
    {
        double sum = 0;
        for (var i = 0; i < interleaved.Length; i++) sum += (double)interleaved[i] * interleaved[i];
        return Math.Sqrt(sum / interleaved.Length);
    }
}
