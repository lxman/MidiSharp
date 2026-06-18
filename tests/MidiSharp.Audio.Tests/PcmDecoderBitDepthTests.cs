using MidiSharp.Audio;
using Xunit;

namespace MidiSharp.Audio.Tests;

/// <summary>
/// Direct unit tests of <see cref="PcmDecoder"/>'s generic integer path for the non-standard container
/// widths that previously returned silence (12/20/48/64-bit). The value is read from the byte container
/// and normalized by the container's full range; a byte-aligned depth is exact.
/// </summary>
public sealed class PcmDecoderBitDepthTests
{
    // Little-endian bytes of a value in a `bytes`-wide container.
    private static byte[] Le(long value, int bytes)
    {
        var b = new byte[bytes];
        for (int i = 0; i < bytes; i++) b[i] = (byte)((value >> (8 * i)) & 0xFF);
        return b;
    }

    [Theory]
    [InlineData(48, 6)]   // exotic byte-aligned
    [InlineData(64, 8)]   // 64-bit integer PCM
    [InlineData(12, 2)]   // sub-byte, 16-bit container
    [InlineData(20, 3)]   // sub-byte, 24-bit container
    public void Generic_depth_decodes_half_scale_and_extremes(int bits, int containerBytes)
    {
        long half = 1L << (containerBytes * 8 - 2);   // 2^(N-2) = +0.5 of full-scale 2^(N-1)
        long min = -(1L << (containerBytes * 8 - 1));  // -2^(N-1) = -1.0

        var (pos, fp) = PcmDecoder.Decode(Le(half, containerBytes), channels: 1, bits, isFloat: false);
        Assert.Equal(1, fp);
        Assert.True(System.Math.Abs(pos[0] - 0.5f) < 1e-4, $"{bits}-bit +half: {pos[0]}");

        var (neg, fn) = PcmDecoder.Decode(Le(min, containerBytes), channels: 1, bits, isFloat: false);
        Assert.Equal(1, fn);
        Assert.True(System.Math.Abs(neg[0] - -1.0f) < 1e-4, $"{bits}-bit min: {neg[0]}");
    }

    [Fact]
    public void Forty_eight_bit_two_frames_decode_in_order()
    {
        // Two 48-bit frames: +0.25 then -0.5.
        long q = 1L << 45;             // 2^45 / 2^47 = 0.25
        long h = -(1L << 46);          // -2^46 / 2^47 = -0.5
        var data = new byte[12];
        Le(q, 6).CopyTo(data, 0);
        Le(h, 6).CopyTo(data, 6);

        var (s, frames) = PcmDecoder.Decode(data, channels: 1, bitsPerSample: 48, isFloat: false);
        Assert.Equal(2, frames);
        Assert.True(System.Math.Abs(s[0] - 0.25f) < 1e-4, $"{s[0]}");
        Assert.True(System.Math.Abs(s[1] - -0.5f) < 1e-4, $"{s[1]}");
    }

    [Fact]
    public void Above_64_bit_container_returns_empty()
    {
        var (s, frames) = PcmDecoder.Decode(new byte[16], channels: 1, bitsPerSample: 72, isFloat: false);
        Assert.Equal(0, frames);
        Assert.Empty(s);
    }

    [Fact]
    public void Standard_depths_still_decode_through_their_fast_paths()
    {
        // 16-bit half-scale stays exact (guards against the generic path disturbing the dedicated cases).
        var (s, _) = PcmDecoder.Decode(Le(1L << 14, 2), channels: 1, bitsPerSample: 16, isFloat: false);
        Assert.True(System.Math.Abs(s[0] - 0.5f) < 1e-4, $"{s[0]}");
    }
}
