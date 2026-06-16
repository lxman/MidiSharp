using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MidiSharp.Audio;

/// <summary>
/// Bulk PCM→float conversion helpers, SIMD-accelerated where the hardware supports it.
/// </summary>
public static class SampleConvert
{
    /// <summary>
    /// Convert little-endian 16-bit PCM (already host-order <see cref="short"/>s) to float, scaled.
    /// Vectorized via <see cref="Vector{T}"/> with a scalar tail. Bit-identical to <c>src[i] * scale</c>:
    /// short→int (Widen) then int→float (ConvertToSingle) is exact across the 16-bit range, matching
    /// the scalar promotion. Processes <c>min(src.Length, dest.Length)</c> samples.
    /// </summary>
    public static void Int16ToFloat(ReadOnlySpan<short> src, Span<float> dest, float scale)
    {
        var n = Math.Min(src.Length, dest.Length);
        var i = 0;

        if (Vector.IsHardwareAccelerated && n >= Vector<short>.Count)
        {
            var step = Vector<short>.Count;        // e.g. 16 shorts → two Vector<int>/Vector<float>
            var half = Vector<int>.Count;
            var vScale = new Vector<float>(scale);
            var srcBytes = MemoryMarshal.AsBytes(src);
            var dstBytes = MemoryMarshal.AsBytes(dest);
            var last = n - step;
            for (; i <= last; i += step)
            {
                var s = MemoryMarshal.Read<Vector<short>>(srcBytes.Slice(i * sizeof(short)));
                Vector.Widen(s, out Vector<int> lo, out Vector<int> hi);
                var fLo = Vector.ConvertToSingle(lo) * vScale;
                var fHi = Vector.ConvertToSingle(hi) * vScale;
                MemoryMarshal.Write(dstBytes.Slice(i * sizeof(float)), ref fLo);
                MemoryMarshal.Write(dstBytes.Slice((i + half) * sizeof(float)), ref fHi);
            }
        }

        for (; i < n; i++) dest[i] = src[i] * scale;
    }
}
