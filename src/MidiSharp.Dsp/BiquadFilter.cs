using System;

namespace MidiSharp.Dsp;

/// <summary>The response shape of a <see cref="BiquadFilter"/> section.</summary>
public enum BiquadType
{
    /// <summary>Low shelf: boost/cut everything below <c>freq</c> by <c>gainDb</c>.</summary>
    LowShelf,
    /// <summary>High shelf: boost/cut everything above <c>freq</c> by <c>gainDb</c>.</summary>
    HighShelf,
    /// <summary>Peaking/bell: boost/cut a band around <c>freq</c> (width set by <c>q</c>) by <c>gainDb</c>.</summary>
    Peaking,
    /// <summary>Resonant low-pass (gain ignored).</summary>
    LowPass,
    /// <summary>Resonant high-pass (gain ignored).</summary>
    HighPass,
    /// <summary>Band-reject notch (gain ignored).</summary>
    Notch,
}

/// <summary>
/// One RBJ-cookbook biquad section for a single audio channel, in Transposed Direct Form II. Derived
/// clean-room from the public Audio-EQ-Cookbook difference equations (Robert Bristow-Johnson). Holds
/// per-channel state, so use one instance per channel; reconfiguring keeps the running state (a smooth
/// coefficient change) while <see cref="Reset"/> clears it.
/// </summary>
public sealed class BiquadFilter
{
    private readonly int _sampleRate;

    // Normalized coefficients (a0 divided out).
    private double _b0 = 1.0, _b1, _b2, _a1, _a2;

    // Transposed Direct Form II state.
    private double _z1, _z2;

    public BiquadFilter(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
    }

    /// <summary>Makes this section a pass-through (b0=1, all else 0). State is preserved.</summary>
    public void SetIdentity()
    {
        _b0 = 1.0; _b1 = _b2 = _a1 = _a2 = 0.0;
    }

    /// <summary>Clears the filter's delay state without touching its coefficients.</summary>
    public void Reset() => _z1 = _z2 = 0.0;

    /// <summary>
    /// Computes coefficients for the given response. <paramref name="freqHz"/> outside (0, Nyquist)
    /// or a non-positive <paramref name="q"/> degrades gracefully to a pass-through. <paramref name="gainDb"/>
    /// is used only by the shelf and peaking types.
    /// </summary>
    public void Configure(BiquadType type, double freqHz, double q, double gainDb)
    {
        double nyquist = _sampleRate * 0.5;
        if (freqHz <= 0.0 || freqHz >= nyquist) { SetIdentity(); return; }
        if (q <= 0.0) q = 1e-4;

        double a = Math.Pow(10.0, gainDb / 40.0);      // shelf/peak linear amplitude
        double w0 = 2.0 * Math.PI * freqHz / _sampleRate;
        double cw = Math.Cos(w0);
        double sw = Math.Sin(w0);
        double alpha = sw / (2.0 * q);
        double sqrtA = Math.Sqrt(a);

        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case BiquadType.LowPass:
                b0 = (1.0 - cw) / 2.0; b1 = 1.0 - cw; b2 = (1.0 - cw) / 2.0;
                a0 = 1.0 + alpha;      a1 = -2.0 * cw; a2 = 1.0 - alpha;
                break;
            case BiquadType.HighPass:
                b0 = (1.0 + cw) / 2.0; b1 = -(1.0 + cw); b2 = (1.0 + cw) / 2.0;
                a0 = 1.0 + alpha;      a1 = -2.0 * cw;   a2 = 1.0 - alpha;
                break;
            case BiquadType.Notch:
                b0 = 1.0;         b1 = -2.0 * cw; b2 = 1.0;
                a0 = 1.0 + alpha; a1 = -2.0 * cw; a2 = 1.0 - alpha;
                break;
            case BiquadType.Peaking:
                b0 = 1.0 + alpha * a; b1 = -2.0 * cw;     b2 = 1.0 - alpha * a;
                a0 = 1.0 + alpha / a; a1 = -2.0 * cw;     a2 = 1.0 - alpha / a;
                break;
            case BiquadType.LowShelf:
                b0 = a * ((a + 1.0) - (a - 1.0) * cw + 2.0 * sqrtA * alpha);
                b1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cw);
                b2 = a * ((a + 1.0) - (a - 1.0) * cw - 2.0 * sqrtA * alpha);
                a0 = (a + 1.0) + (a - 1.0) * cw + 2.0 * sqrtA * alpha;
                a1 = -2.0 * ((a - 1.0) + (a + 1.0) * cw);
                a2 = (a + 1.0) + (a - 1.0) * cw - 2.0 * sqrtA * alpha;
                break;
            case BiquadType.HighShelf:
                b0 = a * ((a + 1.0) + (a - 1.0) * cw + 2.0 * sqrtA * alpha);
                b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cw);
                b2 = a * ((a + 1.0) + (a - 1.0) * cw - 2.0 * sqrtA * alpha);
                a0 = (a + 1.0) - (a - 1.0) * cw + 2.0 * sqrtA * alpha;
                a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cw);
                a2 = (a + 1.0) - (a - 1.0) * cw - 2.0 * sqrtA * alpha;
                break;
            default:
                SetIdentity();
                return;
        }

        double inv = 1.0 / a0;
        _b0 = b0 * inv; _b1 = b1 * inv; _b2 = b2 * inv;
        _a1 = a1 * inv; _a2 = a2 * inv;
    }

    /// <summary>Processes one sample through the section.</summary>
    public double Process(double x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }
}
