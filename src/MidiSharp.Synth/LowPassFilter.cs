using System;

namespace MidiSharp.Synth;

/// <summary>
/// Resonant low-pass filter (2-pole, 12dB/octave).
/// Based on the classic SF2 filter design.
/// </summary>
public sealed class LowPassFilter
{
    private double _a0, _a1, _a2, _b1, _b2;
    private double _z1, _z2;
    private readonly int _sampleRate;
    private double _cutoffFrequency;
    private double _resonance;
    private bool _enabled;

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
    /// Sets filter parameters from SF2 generator values.
    /// </summary>
    /// <param name="cutoffCents">Cutoff frequency in absolute cents (1500 = 100Hz to 13500 = 20kHz)</param>
    /// <param name="resonanceCentibels">Resonance in centibels (0 to 960)</param>
    public void SetParameters(short cutoffCents, short resonanceCentibels)
    {
        // Convert cents to Hz: 8.176 * 2^(cents/1200)
        _cutoffFrequency = 8.176 * Math.Pow(2.0, cutoffCents / 1200.0);

        // Clamp to reasonable range
        _cutoffFrequency = Math.Clamp(_cutoffFrequency, 20, _sampleRate * 0.45);

        // Convert centibels to Q factor
        // SF2: 0 cb = no resonance (Q=1), 960 cb = max resonance (Q~96)
        // Q = 10^(cb/200)
        _resonance = Math.Pow(10.0, resonanceCentibels / 200.0);
        _resonance = Math.Clamp(_resonance, 0.5, 40.0);

        // Enable filter if cutoff is below Nyquist
        _enabled = _cutoffFrequency < _sampleRate * 0.45;

        if (_enabled)
            CalculateCoefficients();
    }

    /// <summary>
    /// Sets filter parameters directly.
    /// </summary>
    public void SetParameters(double cutoffHz, double q)
    {
        _cutoffFrequency = Math.Clamp(cutoffHz, 20, _sampleRate * 0.45);
        _resonance = Math.Clamp(q, 0.5, 40.0);
        _enabled = _cutoffFrequency < _sampleRate * 0.45;

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
        // Compute biquad low-pass filter coefficients
        var omega = 2.0 * Math.PI * frequency / _sampleRate;
        var sinOmega = Math.Sin(omega);
        var cosOmega = Math.Cos(omega);
        var alpha = sinOmega / (2.0 * _resonance);

        var a0 = 1.0 + alpha;
        _b1 = -2.0 * cosOmega / a0;
        _b2 = (1.0 - alpha) / a0;

        var b0 = (1.0 - cosOmega) / 2.0;
        _a0 = b0 / a0;
        _a1 = (1.0 - cosOmega) / a0;
        _a2 = b0 / a0;
    }
}
