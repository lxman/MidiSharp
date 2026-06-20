using System;
using MidiSharp.Dsp;
using MidiSharp.Hosting;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// The format-agnostic host machinery, proven end-to-end against <see cref="FakeGainPlugin"/>: the
/// interleaved↔planar bridge is arithmetically exact, an identity plugin is bit-identical (Phase-0
/// invariant 1), the realtime path allocates nothing (invariant 2), oversized blocks chunk correctly,
/// and a HostedEffect drops into a Dsp ProcessorChain unchanged.
/// </summary>
public sealed class HostedEffectTests
{
    private static readonly AudioConfig Config = new(SampleRate: 48000, MaxBlockFrames: 128, ChannelCount: 2);

    private static float[] Ramp(int frames)
    {
        var buf = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            buf[2 * i] = 0.5f - i * 0.001f;       // left
            buf[2 * i + 1] = -0.5f + i * 0.0015f; // right
        }
        return buf;
    }

    private static HostedEffect Wrap(double normalizedGain, AudioConfig? config = null)
    {
        var cfg = config ?? Config;
        var plugin = new FakeGainPlugin();
        plugin.Activate(cfg);
        plugin.SetParameter(0, normalizedGain);
        return new HostedEffect(plugin, cfg);
    }

    [Fact]
    public void Applies_the_plugin_gain_through_the_bridge()
    {
        using var effect = Wrap(0.25);          // 0.25 * 2.0 range = ×0.5
        var buf = Ramp(128);
        var expected = (float[])buf.Clone();
        for (var i = 0; i < expected.Length; i++) expected[i] *= 0.5f;

        effect.Process(buf);
        Assert.Equal(expected, buf);
    }

    [Fact]
    public void Identity_gain_is_bit_identical()
    {
        using var effect = Wrap(0.5);           // 0.5 * 2.0 = ×1.0
        var buf = Ramp(128);
        var original = (float[])buf.Clone();

        effect.Process(buf);
        Assert.Equal(original, buf);            // exact sample-for-sample
    }

    [Fact]
    public void Realtime_path_allocates_nothing()
    {
        using var effect = Wrap(0.25);
        var buf = Ramp(128);

        effect.Process(buf);                    // warm up (JIT, first-touch)
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var k = 0; k < 100; k++) effect.Process(buf);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Blocks_larger_than_max_are_chunked_correctly()
    {
        var cfg = Config with { MaxBlockFrames = 64 };
        using var effect = Wrap(0.25, cfg);     // ×0.5
        var buf = Ramp(200);                    // > MaxBlockFrames → 4 chunks (64,64,64,8)
        var expected = (float[])buf.Clone();
        for (var i = 0; i < expected.Length; i++) expected[i] *= 0.5f;

        effect.Process(buf);
        Assert.Equal(expected, buf);
    }

    [Fact]
    public void Drops_into_a_processor_chain()
    {
        using var effect = Wrap(0.25);          // ×0.5
        var chain = new ProcessorChain();
        chain.Add(effect);

        var buf = Ramp(128);
        var expected = (float[])buf.Clone();
        for (var i = 0; i < expected.Length; i++) expected[i] *= 0.5f;

        chain.Process(buf);                     // exercised as an IAudioProcessor
        Assert.Equal(expected, buf);
    }
}
