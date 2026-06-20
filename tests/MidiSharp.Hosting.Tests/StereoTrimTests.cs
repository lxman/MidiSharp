using System;
using MidiSharp.Hosting;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// The per-part gain/pan trim applied when a hosted plugin instrument is summed into the mix. Measured:
/// gain is proportional in linear terms (×0.5 / ×1 / ×2 for −6 / 0 / +6 dB), pan is a balance offset that
/// hard-pans to one channel at ±1, and an untouched part (0 dB, pan 0) is a bit-identical plain add.
/// </summary>
public sealed class StereoTrimTests
{
    private static float[] Stereo(float l, float r, int frames)
    {
        var buf = new float[frames * 2];
        for (var i = 0; i < frames; i++) { buf[2 * i] = l; buf[2 * i + 1] = r; }
        return buf;
    }

    private static (double l, double r) Rms(ReadOnlySpan<float> interleaved)
    {
        double sl = 0, sr = 0; var frames = interleaved.Length / 2;
        for (var i = 0; i < frames; i++) { sl += (double)interleaved[2 * i] * interleaved[2 * i]; sr += (double)interleaved[2 * i + 1] * interleaved[2 * i + 1]; }
        return (Math.Sqrt(sl / frames), Math.Sqrt(sr / frames));
    }

    [Fact]
    public void Unity_trim_is_a_bit_identical_plain_add()
    {
        var dst = Stereo(0.1f, -0.2f, 256);
        var src = Stereo(0.3f, 0.4f, 256);
        StereoTrim.Add(dst, src, gainDb: 0.0, pan: 0.0);
        for (var i = 0; i < 256; i++)
        {
            Assert.Equal(0.1f + 0.3f, dst[2 * i]);       // exact float add — no scaling applied
            Assert.Equal(-0.2f + 0.4f, dst[2 * i + 1]);
        }
    }

    [Theory]
    [InlineData(-6.0206, 0.5)]   // −6.02 dB → ×0.5
    [InlineData(0.0, 1.0)]
    [InlineData(6.0206, 2.0)]    // +6.02 dB → ×2
    public void Gain_scales_proportionally(double gainDb, double factor)
    {
        var dst = new float[256 * 2];
        var src = Stereo(0.25f, 0.25f, 256);
        StereoTrim.Add(dst, src, gainDb, pan: 0.0);
        var (l, r) = Rms(dst);
        Assert.Equal(0.25 * factor, l, 3);
        Assert.Equal(0.25 * factor, r, 3);
    }

    [Fact]
    public void Hard_pan_right_silences_the_left_channel()
    {
        var dst = new float[256 * 2];
        var src = Stereo(0.3f, 0.3f, 256);
        StereoTrim.Add(dst, src, gainDb: 0.0, pan: 1.0);
        var (l, r) = Rms(dst);
        Assert.Equal(0.0, l, 6);
        Assert.Equal(0.3, r, 6);
    }

    [Fact]
    public void Hard_pan_left_silences_the_right_channel()
    {
        var dst = new float[256 * 2];
        var src = Stereo(0.3f, 0.3f, 256);
        StereoTrim.Add(dst, src, gainDb: 0.0, pan: -1.0);
        var (l, r) = Rms(dst);
        Assert.Equal(0.3, l, 6);
        Assert.Equal(0.0, r, 6);
    }
}
