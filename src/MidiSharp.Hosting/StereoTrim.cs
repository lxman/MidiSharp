using System;

namespace MidiSharp.Hosting;

/// <summary>
/// Sums a hosted source's interleaved-stereo output into a destination mix with a per-part <b>trim</b>:
/// a gain in dB and a pan offset in -1..+1. The trim mirrors the synth's per-instrument mixer semantics
/// so a plugin instrument summed alongside synth voices answers to the same fader: gain rides on top
/// (dB→linear factor) and pan is a stereo <i>balance</i> offset (neutral 0 leaves both channels alone,
/// +1 hard-right attenuates the left to silence, -1 hard-left attenuates the right). Mute/solo gating is
/// the caller's decision (it depends on the whole mix), so a gated source simply isn't summed.
/// </summary>
public static class StereoTrim
{
    /// <summary>
    /// Accumulate <paramref name="src"/> (interleaved L,R,…) into <paramref name="dst"/> applying
    /// <paramref name="gainDb"/> and <paramref name="pan"/>. At gain 0 dB and pan 0 this is a plain add
    /// (×1.0, both channels), so an untouched part sums bit-identically to a naive accumulate. Processes
    /// <c>min(dst,src)</c> whole stereo frames.
    /// </summary>
    public static void Add(Span<float> dst, ReadOnlySpan<float> src, double gainDb, double pan)
    {
        (float gl, float gr) = Factors(gainDb, pan);
        int n = Math.Min(dst.Length, src.Length);
        for (var i = 0; i + 1 < n; i += 2)
        {
            dst[i] += src[i] * gl;
            dst[i + 1] += src[i + 1] * gr;
        }
    }

    /// <summary>
    /// Apply the same gain+pan trim to <paramref name="buf"/> <b>in place</b> (no accumulation). Used when
    /// a hosted instrument has a per-part insert chain: the part's signal is trimmed pre-insert (matching
    /// the synth, which applies the fader at the voice before the instrument's bus insert), the insert runs
    /// on the trimmed signal, and the result is then summed into the mix.
    /// </summary>
    public static void Apply(Span<float> buf, double gainDb, double pan)
    {
        (float gl, float gr) = Factors(gainDb, pan);
        if (gl == 1f && gr == 1f) return;   // unity → leave untouched (bit-identical)
        for (var i = 0; i + 1 < buf.Length; i += 2)
        {
            buf[i] *= gl;
            buf[i + 1] *= gr;
        }
    }

    private static (float left, float right) Factors(double gainDb, double pan)
    {
        float gain = gainDb == 0.0 ? 1f : (float)Math.Pow(10.0, gainDb / 20.0);
        float gl = pan > 0 ? gain * (float)(1.0 - pan) : gain;   // pan right → trim left
        float gr = pan < 0 ? gain * (float)(1.0 + pan) : gain;   // pan left  → trim right
        return (gl, gr);
    }
}
