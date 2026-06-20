using System;
using MidiSharp.Hosting;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>The interleaved↔planar kernel every format adapter relies on: a round-trip is exact.</summary>
public sealed class PlanarBridgeTests
{
    [Fact]
    public void Deinterleave_then_interleave_round_trips_exactly()
    {
        const int frames = 64;
        var interleaved = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            interleaved[2 * i] = i * 0.01f;          // left ramp
            interleaved[2 * i + 1] = -i * 0.02f;     // right ramp
        }
        var original = (float[])interleaved.Clone();

        var left = new float[frames];
        var right = new float[frames];
        PlanarBridge.DeinterleaveStereo(interleaved, left, right);

        // Channels are the de-interleaved samples.
        for (var i = 0; i < frames; i++)
        {
            Assert.Equal(original[2 * i], left[i]);
            Assert.Equal(original[2 * i + 1], right[i]);
        }

        Array.Clear(interleaved);
        PlanarBridge.InterleaveStereo(left, right, interleaved);
        Assert.Equal(original, interleaved);   // bit-exact round trip
    }

    [Fact]
    public void Mismatched_buffer_throws()
    {
        var interleaved = new float[128];
        Assert.Throws<ArgumentException>(() =>
            PlanarBridge.DeinterleaveStereo(interleaved, new float[10], new float[64]));
    }
}
