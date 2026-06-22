using System;
using Xunit;

namespace MidiSharp.Dsp.Tests;

/// <summary>
/// Proves the limiter is a true brickwall (output never exceeds the ceiling), passes signal that's
/// already under the ceiling untouched, keeps the stereo channels linked, and recovers after a peak.
/// </summary>
public sealed class LimiterProcessorTests
{
    private const int Rate = 48000;

    [Fact]
    public void Output_never_exceeds_the_ceiling()
    {
        var lim = new LimiterProcessor(Rate) { CeilingDb = -1.0 };   // ceiling ≈ 0.891
        double ceiling = Math.Pow(10, -1.0 / 20.0);
        float[] buf = Tone(220, amp: 0.95f, frames: 8192);               // hot, above ceiling
        lim.Process(buf);
        float peak = Peak(buf);
        Assert.True(peak <= ceiling + 1e-4f, $"peak {peak:F4} must not exceed ceiling {ceiling:F4}");
    }

    [Fact]
    public void Disabled_limiter_is_a_passthrough_even_over_the_ceiling()
    {
        var lim = new LimiterProcessor(Rate) { CeilingDb = -6.0, Enabled = false };
        float[] buf = Tone(220, amp: 0.99f, frames: 2048);   // way over the ceiling
        var copy = (float[])buf.Clone();
        lim.Process(buf);
        Assert.Equal(copy, buf);   // disabled → untouched
    }

    [Fact]
    public void Signal_under_the_ceiling_passes_untouched()
    {
        var lim = new LimiterProcessor(Rate) { CeilingDb = -1.0 };
        float[] buf = Tone(220, amp: 0.3f, frames: 4096);               // well under the ceiling
        var copy = (float[])buf.Clone();
        lim.Process(buf);
        Assert.Equal(copy, buf);   // unity gain, bit-identical
    }

    [Fact]
    public void Stereo_stays_linked()
    {
        var lim = new LimiterProcessor(Rate) { CeilingDb = -3.0 };
        // Asymmetric block: left hot, right quiet. The shared gain must scale both equally, so their
        // ratio is preserved sample-for-sample.
        var frames = 2048;
        var buf = new float[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            buf[f * 2] = (float)(0.95 * Math.Sin(2 * Math.PI * 200 * f / Rate));
            buf[f * 2 + 1] = buf[f * 2] * 0.5f;
        }
        lim.Process(buf);
        for (var f = 0; f < frames; f++)
            if (Math.Abs(buf[f * 2]) > 1e-6f)
                Assert.True(Math.Abs(buf[f * 2 + 1] / buf[f * 2] - 0.5f) < 1e-3f, "L/R ratio must be preserved");
    }

    [Fact]
    public void Gain_recovers_after_a_peak()
    {
        var lim = new LimiterProcessor(Rate) { CeilingDb = -1.0, ReleaseMs = 50 };
        lim.Process(Tone(200, 0.99f, 4096));        // drive it into reduction
        Assert.True(lim.GainReductionDb < -0.1, "should be reducing during a hot signal");
        // Now feed silence-ish low level long enough to release.
        for (var i = 0; i < 20; i++) lim.Process(Tone(200, 0.05f, 4096));
        Assert.True(lim.GainReductionDb > -0.01, $"should recover to ~unity (got {lim.GainReductionDb:F3} dB)");
    }

    private static float[] Tone(double freqHz, float amp, int frames)
    {
        var buf = new float[frames * 2];
        double step = 2.0 * Math.PI * freqHz / Rate;
        for (var f = 0; f < frames; f++)
        {
            var s = (float)(amp * Math.Sin(step * f));
            buf[f * 2] = s; buf[f * 2 + 1] = s;
        }
        return buf;
    }

    private static float Peak(float[] x)
    {
        float p = 0;
        foreach (float v in x) p = Math.Max(p, Math.Abs(v));
        return p;
    }
}
