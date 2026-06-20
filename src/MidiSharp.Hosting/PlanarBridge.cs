using System;

namespace MidiSharp.Hosting;

/// <summary>
/// The realtime kernel that bridges MidiSharp's interleaved-stereo audio path to the planar
/// (non-interleaved) <c>float**</c> every plugin format speaks. Written once here and reused by every
/// format adapter via <see cref="HostedEffect"/>:
/// <code>
///   deinterleave(block) -> planar in  ->  plugin.process  ->  planar out -> interleave(block)
/// </code>
/// All methods are allocation-free and operate on caller-owned spans, so they are safe on the audio
/// thread. Stereo is the current engine's bus; the general N-channel split/merge is a later concern.
/// </summary>
public static class PlanarBridge
{
    /// <summary>
    /// Split an interleaved stereo block into two channel buffers.
    /// <paramref name="interleaved"/> length is <c>frames × 2</c> (index 2i = left, 2i+1 = right);
    /// <paramref name="left"/>/<paramref name="right"/> each receive <c>frames</c> samples.
    /// </summary>
    public static void DeinterleaveStereo(ReadOnlySpan<float> interleaved, Span<float> left, Span<float> right)
    {
        var frames = interleaved.Length / 2;
        if (left.Length < frames || right.Length < frames)
            throw new ArgumentException("Channel buffers are smaller than the block's frame count.");

        for (var i = 0; i < frames; i++)
        {
            left[i] = interleaved[2 * i];
            right[i] = interleaved[2 * i + 1];
        }
    }

    /// <summary>
    /// Merge two channel buffers back into an interleaved stereo block, in place.
    /// <paramref name="interleaved"/> length is <c>frames × 2</c>; it reads <c>frames</c> samples from
    /// each of <paramref name="left"/>/<paramref name="right"/>.
    /// </summary>
    public static void InterleaveStereo(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        if (left.Length < frames || right.Length < frames)
            throw new ArgumentException("Channel buffers are smaller than the block's frame count.");

        for (var i = 0; i < frames; i++)
        {
            interleaved[2 * i] = left[i];
            interleaved[2 * i + 1] = right[i];
        }
    }
}
