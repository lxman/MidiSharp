using System;

namespace MidiSharp.Synth;

/// <summary>
/// Precomputed 7-point windowed-sinc interpolation table. Used by Voice for
/// high-quality sample rate conversion. The table is built once at type-init.
///
/// 7-point sinc preserves significantly more high-frequency content than 4-point
/// Hermite, especially for samples played at non-integer pitch ratios. The cost
/// is 7 multiplies per output sample (vs 3 for Hermite), all served by a small
/// precomputed coefficient table.
/// </summary>
internal static class SincInterpolator
{
    public const int Width = 7;          // 7-point sinc
    private const int HalfWidth = Width / 2;       // = 3
    private const int FractionBits = 8;            // 256 fractional positions
    public const int FractionSlots = 1 << FractionBits;    // 256
    public const int FractionMask = FractionSlots - 1;

    // Layout: [fraction_index, tap_index] flattened to [fraction_index * Width + tap].
    // The tap_index goes 0..Width-1, corresponding to source-sample offsets -HalfWidth..+HalfWidth.
    private static readonly float[] _coeffs;

    /// <summary>
    /// Read-only access to the flattened coefficient table for performance-critical
    /// inlined evaluation. Voice does this in its inner sample loop to avoid a
    /// per-sample delegate allocation.
    /// </summary>
    internal static float[] Coefficients => _coeffs;

    static SincInterpolator()
    {
        _coeffs = new float[FractionSlots * Width];

        for (var f = 0; f < FractionSlots; f++)
        {
            double frac = (double)f / FractionSlots;       // 0 .. just-under-1
            double sum = 0;
            for (var i = 0; i < Width; i++)
            {
                // x is the distance from the interpolation point to source sample (i - HalfWidth).
                // At frac=0 we're sitting exactly on tap i=HalfWidth; positive frac moves toward i=HalfWidth+1.
                double x = i - HalfWidth - frac;
                double s = Sinc(x);
                double w = Blackman(x, Width);
                double v = s * w;
                _coeffs[f * Width + i] = (float)v;
                sum += v;
            }
            // Normalize so weights sum to 1 (eliminates unity-gain offset across fractions).
            var inv = (float)(1.0 / sum);
            for (var i = 0; i < Width; i++)
                _coeffs[f * Width + i] *= inv;
        }
    }

    /// <summary>
    /// Interpolates at a fractional position given a sample-reader delegate that
    /// returns the value at any absolute frame index (with the caller's loop /
    /// boundary handling applied).
    /// </summary>
    /// <param name="centreIndex">Integer part of the position (the sample at frac=0).</param>
    /// <param name="frac">Fractional part [0, 1).</param>
    /// <param name="reader">Reads sample at an absolute frame index.</param>
    public static double Interpolate(int centreIndex, double frac, Func<int, double> reader)
    {
        var fIdx = (int)(frac * FractionSlots);
        if (fIdx >= FractionSlots) fIdx = FractionSlots - 1;
        int baseOff = fIdx * Width;

        double sum = 0;
        for (var i = 0; i < Width; i++)
        {
            sum += _coeffs[baseOff + i] * reader(centreIndex + i - HalfWidth);
        }
        return sum;
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-12) return 1.0;
        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    /// <summary>Blackman window centred on x=0, zero outside [-width/2, +width/2].</summary>
    private static double Blackman(double x, int width)
    {
        double half = width / 2.0;
        if (x <= -half || x >= half) return 0;
        // Map x in [-half, half] to t in [0, 1]
        double t = (x + half) / width;
        return 0.42 - 0.5 * Math.Cos(2 * Math.PI * t) + 0.08 * Math.Cos(4 * Math.PI * t);
    }
}
