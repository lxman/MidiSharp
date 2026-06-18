using System;
using MidiSharp.SoundBank;
using MidiSharp.Synth;
using Xunit;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Verifies the RBJ low-shelf / high-shelf / peaking biquads (SFZ lsh/hsh/peq + fil_gain) actually shape
/// the spectrum — a sine in the boosted band gains ~fil_gain dB, one outside stays flat — rather than the
/// old behaviour of falling back to a low-pass.
/// </summary>
public sealed class ShelfFilterTests
{
    private const int Rate = 44100;

    [Fact]
    public void Low_shelf_boosts_lows_leaves_highs()
    {
        var f = new LowPassFilter(Rate) { Type = FilterType.LowShelf, GainDb = 12.0 };
        f.SetParameters(cutoffHz: 1000.0, resonanceDb: 0.0);
        Assert.InRange(MeasureGainDb(f, 80.0), 10.5, 13.5);    // a decade below the corner ≈ full boost
        Assert.InRange(MeasureGainDb(f, 12000.0), -1.0, 1.5);  // well above ≈ unchanged
    }

    [Fact]
    public void High_shelf_boosts_highs_leaves_lows()
    {
        var f = new LowPassFilter(Rate) { Type = FilterType.HighShelf, GainDb = 12.0 };
        f.SetParameters(cutoffHz: 1000.0, resonanceDb: 0.0);
        Assert.InRange(MeasureGainDb(f, 12000.0), 10.5, 13.5);
        Assert.InRange(MeasureGainDb(f, 80.0), -1.0, 1.5);
    }

    [Fact]
    public void Low_shelf_cut_attenuates_lows()
    {
        var f = new LowPassFilter(Rate) { Type = FilterType.LowShelf, GainDb = -12.0 };
        f.SetParameters(cutoffHz: 1000.0, resonanceDb: 0.0);
        Assert.InRange(MeasureGainDb(f, 80.0), -13.5, -10.5);
        Assert.InRange(MeasureGainDb(f, 12000.0), -1.5, 1.0);
    }

    [Fact]
    public void Peaking_boosts_near_center_only()
    {
        var f = new LowPassFilter(Rate) { Type = FilterType.Peaking, GainDb = 12.0 };
        f.SetParameters(cutoffHz: 1000.0, resonanceDb: 6.0);   // moderate Q
        Assert.InRange(MeasureGainDb(f, 1000.0), 9.0, 13.0);   // at the centre ≈ full boost
        Assert.InRange(MeasureGainDb(f, 80.0), -1.5, 1.5);     // far below ≈ flat
        Assert.InRange(MeasureGainDb(f, 12000.0), -1.5, 1.5);  // far above ≈ flat
    }

    [Fact]
    public void Zero_gain_shelf_is_flat_passthrough()
    {
        var f = new LowPassFilter(Rate) { Type = FilterType.LowShelf, GainDb = 0.0 };
        f.SetParameters(cutoffHz: 1000.0, resonanceDb: 0.0);
        Assert.InRange(MeasureGainDb(f, 80.0), -0.2, 0.2);
        Assert.InRange(MeasureGainDb(f, 5000.0), -0.2, 0.2);
    }

    /// <summary>Output/input RMS ratio (dB) of a steady sine at <paramref name="freq"/> through the filter.</summary>
    private static double MeasureGainDb(LowPassFilter f, double freq)
    {
        const int n = Rate;                  // 1 s — long enough for steady state
        double inSumSq = 0, outSumSq = 0;
        for (int i = 0; i < n; i++)
        {
            double x = Math.Sin(2.0 * Math.PI * freq * i / Rate);
            double y = f.Process(x);
            if (i >= Rate / 2)               // measure the second half (skip the warm-up transient)
            {
                inSumSq += x * x;
                outSumSq += y * y;
            }
        }
        return 10.0 * Math.Log10(outSumSq / inSumSq);
    }
}
