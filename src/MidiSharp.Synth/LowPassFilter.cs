using System;
using MidiSharp.SoundBank;

namespace MidiSharp.Synth;

/// <summary>
/// Resonant 2-pole (12 dB/oct) RBJ biquad. Despite the name it supports the SFZ filter types via
/// <see cref="Type"/> (low/high-pass, band-pass, notch, low/high-shelf, peaking); 1-pole SFZ variants
/// are approximated by the 2-pole form. Shelving and peaking types use <see cref="GainDb"/> (SFZ
/// fil_gain). The LowPass path is byte-identical to the original lowpass-only implementation (SF2/DLS
/// and most SFZ filters), which is the default.
/// </summary>
public sealed class LowPassFilter
{
    private double _a0, _a1, _a2, _b1, _b2;
    private double _z1, _z2;
    private readonly int _sampleRate;
    private double _cutoffFrequency;
    private double _resonance;
    private bool _enabled;

    /// <summary>Filter response type. Set before <see cref="SetParameters(double,double)"/>.</summary>
    public FilterType Type { get; set; } = FilterType.LowPass;

    /// <summary>SFZ fil_gain — shelf/peak gain in dB (LowShelf/HighShelf/Peaking). 0 = flat. Ignored by
    /// the pass/notch types. Set before <see cref="SetParameters(double,double)"/>.</summary>
    public double GainDb { get; set; }

    /// <summary>
    /// Whether the filter is enabled.
    /// </summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// Current cutoff frequency in Hz.
    /// </summary>
    public double CutoffFrequency => _cutoffFrequency;

    /// <summary>
    /// Current resonance (Q factor).
    /// </summary>
    public double Resonance => _resonance;

    /// <summary>
    /// Creates a new low-pass filter.
    /// </summary>
    public LowPassFilter(int sampleRate)
    {
        _sampleRate = sampleRate;
        Reset();
    }

    /// <summary>
    /// Resets the filter state.
    /// </summary>
    public void Reset()
    {
        _z1 = 0;
        _z2 = 0;
        _enabled = false;
        _cutoffFrequency = 20000;
        _resonance = 0;
    }

    /// <summary>
    /// Sets filter parameters from SF2 generator values. Delegates to the
    /// domain-typed overload after converting absolute cents → Hz and
    /// centibels → dB.
    /// </summary>
    /// <param name="cutoffCents">Cutoff frequency in absolute cents (1500 = 100Hz to 13500 = 20kHz)</param>
    /// <param name="resonanceCentibels">Resonance in centibels (0 to 960)</param>
    public void SetParameters(short cutoffCents, short resonanceCentibels)
    {
        SetParameters(
            cutoffHz: 8.176 * Math.Pow(2.0, cutoffCents / 1200.0),
            resonanceDb: resonanceCentibels / 10.0);
    }

    /// <summary>
    /// Sets filter parameters in domain-natural units. Preferred over the
    /// SF2-unit overload going forward.
    /// </summary>
    /// <param name="cutoffHz">Cutoff frequency in Hz.</param>
    /// <param name="resonanceDb">Resonance peak height in dB (0 = no resonance).</param>
    public void SetParameters(double cutoffHz, double resonanceDb)
    {
        _cutoffFrequency = Math.Clamp(cutoffHz, 20, _sampleRate * 0.45);

        // SF2 spec §8.1.3: InitialFilterQ is "resonance height in centibels".
        // Q-factor mapping: Q = 10^(dB/20), matching the cb-based formula
        // (Q = 10^(cb/200) = 10^((cb/10)/20)) so behavior is identical when
        // the same physical resonance is expressed in either unit.
        _resonance = Math.Pow(10.0, resonanceDb / 20.0);
        _resonance = Math.Clamp(_resonance, 0.5, 40.0);

        // Low-pass keeps its original bypass-near-Nyquist behaviour (byte-identical); the other types
        // filter at any valid cutoff (e.g. a low high-pass cutoff is the whole point).
        _enabled = Type == FilterType.LowPass ? _cutoffFrequency < _sampleRate * 0.45 : true;

        if (_enabled)
            CalculateCoefficients();
    }

    /// <summary>
    /// Modulates the cutoff frequency.
    /// </summary>
    /// <param name="cents">Modulation amount in cents</param>
    public void ModulateCutoff(double cents)
    {
        if (!_enabled)
            return;

        var modFreq = _cutoffFrequency * Math.Pow(2.0, cents / 1200.0);
        modFreq = Math.Clamp(modFreq, 20, _sampleRate * 0.45);

        // Recalculate coefficients with modulated frequency
        CalculateCoefficients(modFreq);
    }

    /// <summary>
    /// Processes a single sample through the filter.
    /// </summary>
    public double Process(double input)
    {
        if (!_enabled)
            return input;

        // Direct Form II transposed biquad
        var output = _a0 * input + _z1;
        _z1 = _a1 * input - _b1 * output + _z2;
        _z2 = _a2 * input - _b2 * output;

        return output;
    }

    /// <summary>
    /// Processes a buffer of samples in-place.
    /// </summary>
    public void Process(Span<float> buffer)
    {
        if (!_enabled)
            return;

        for (var i = 0; i < buffer.Length; i++)
        {
            double input = buffer[i];
            var output = _a0 * input + _z1;
            _z1 = _a1 * input - _b1 * output + _z2;
            _z2 = _a2 * input - _b2 * output;
            buffer[i] = (float)output;
        }
    }

    private void CalculateCoefficients()
    {
        CalculateCoefficients(_cutoffFrequency);
    }

    private void CalculateCoefficients(double frequency)
    {
        // RBJ cookbook biquad. Numerator (n*) varies by type; denominator is shared. Normalised by a0.
        var omega = 2.0 * Math.PI * frequency / _sampleRate;
        var sinOmega = Math.Sin(omega);
        var cosOmega = Math.Cos(omega);
        var alpha = sinOmega / (2.0 * _resonance);

        // Shelving / peaking biquads have a gain-dependent denominator, so they don't share the
        // normalised form below — compute and set their coefficients directly, then return. (The
        // pass/notch path is left untouched, so LowPass stays byte-identical.)
        if (Type is FilterType.LowShelf or FilterType.HighShelf or FilterType.Peaking)
        {
            SetShelfOrPeakCoefficients(cosOmega, alpha);
            return;
        }

        double n0, n1, n2;
        switch (Type)
        {
            case FilterType.HighPass:
                n0 = (1.0 + cosOmega) / 2.0; n1 = -(1.0 + cosOmega); n2 = (1.0 + cosOmega) / 2.0;
                break;
            case FilterType.BandPass:   // constant 0 dB peak gain
                n0 = alpha; n1 = 0.0; n2 = -alpha;
                break;
            case FilterType.Notch:
                n0 = 1.0; n1 = -2.0 * cosOmega; n2 = 1.0;
                break;
            default:                    // LowPass (and shelf fallback) — identical to the original path
                n0 = (1.0 - cosOmega) / 2.0; n1 = 1.0 - cosOmega; n2 = (1.0 - cosOmega) / 2.0;
                break;
        }

        var a0 = 1.0 + alpha;
        _a0 = n0 / a0;
        _a1 = n1 / a0;
        _a2 = n2 / a0;
        _b1 = -2.0 * cosOmega / a0;
        _b2 = (1.0 - alpha) / a0;
    }

    /// <summary>
    /// RBJ-cookbook coefficients for the gain-dependent biquads (low-shelf, high-shelf, peaking).
    /// <c>A = 10^(gainDb/40)</c> is the shelf/peak amplitude; gain 0 dB ⇒ A = 1 ⇒ a flat pass-through.
    /// </summary>
    private void SetShelfOrPeakCoefficients(double cosOmega, double alpha)
    {
        var A = Math.Pow(10.0, GainDb / 40.0);
        double b0, b1, b2, a0, a1, a2;

        if (Type == FilterType.Peaking)
        {
            b0 = 1.0 + alpha * A;
            b1 = -2.0 * cosOmega;
            b2 = 1.0 - alpha * A;
            a0 = 1.0 + alpha / A;
            a1 = -2.0 * cosOmega;
            a2 = 1.0 - alpha / A;
        }
        else
        {
            var sqrtA = Math.Sqrt(A);
            var twoSqrtAalpha = 2.0 * sqrtA * alpha;
            double am1 = A - 1.0, ap1 = A + 1.0;
            if (Type == FilterType.LowShelf)
            {
                b0 = A * (ap1 - am1 * cosOmega + twoSqrtAalpha);
                b1 = 2.0 * A * (am1 - ap1 * cosOmega);
                b2 = A * (ap1 - am1 * cosOmega - twoSqrtAalpha);
                a0 = ap1 + am1 * cosOmega + twoSqrtAalpha;
                a1 = -2.0 * (am1 + ap1 * cosOmega);
                a2 = ap1 + am1 * cosOmega - twoSqrtAalpha;
            }
            else // HighShelf
            {
                b0 = A * (ap1 + am1 * cosOmega + twoSqrtAalpha);
                b1 = -2.0 * A * (am1 + ap1 * cosOmega);
                b2 = A * (ap1 + am1 * cosOmega - twoSqrtAalpha);
                a0 = ap1 - am1 * cosOmega + twoSqrtAalpha;
                a1 = 2.0 * (am1 - ap1 * cosOmega);
                a2 = ap1 - am1 * cosOmega - twoSqrtAalpha;
            }
        }

        _a0 = b0 / a0;
        _a1 = b1 / a0;
        _a2 = b2 / a0;
        _b1 = a1 / a0;
        _b2 = a2 / a0;
    }
}
