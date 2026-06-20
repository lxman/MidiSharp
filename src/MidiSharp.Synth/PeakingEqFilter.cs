using System;

namespace MidiSharp.Synth;

/// <summary>
/// One peaking (bell) EQ band — an RBJ-cookbook biquad in the same Direct-Form-II-transposed shape as
/// <see cref="LowPassFilter"/>. SFZ <c>eqN_freq</c> / <c>eqN_bw</c> / <c>eqN_gain</c> map here: centre
/// frequency (Hz), bandwidth (octaves), peak gain (dB). A 0 dB band is disabled (pass-through).
/// </summary>
public sealed class PeakingEqFilter
{
    private double _a0, _a1, _a2, _b1, _b2;
    private double _z1, _z2;
    private readonly int _sampleRate;
    private bool _enabled;

    public bool Enabled => _enabled;

    public PeakingEqFilter(int sampleRate)
    {
        _sampleRate = sampleRate;
        Reset();
    }

    public void Reset()
    {
        _z1 = 0;
        _z2 = 0;
        _enabled = false;
    }

    /// <summary>
    /// Configures the band. A gain of 0 dB (or a non-positive frequency) disables it so the sample
    /// passes through untouched.
    /// </summary>
    public void SetParameters(double frequencyHz, double bandwidthOctaves, double gainDb)
    {
        if (gainDb == 0.0 || frequencyHz <= 0.0)
        {
            _enabled = false;
            return;
        }

        var f = Math.Clamp(frequencyHz, 20.0, _sampleRate * 0.45);
        var bw = Math.Max(0.001, bandwidthOctaves);
        var a = Math.Pow(10.0, gainDb / 40.0);
        var w0 = 2.0 * Math.PI * f / _sampleRate;
        var cosw0 = Math.Cos(w0);
        var sinw0 = Math.Sin(w0);
        // RBJ peaking EQ, bandwidth specified in octaves.
        var alpha = sinw0 * Math.Sinh(Math.Log(2.0) / 2.0 * bw * w0 / sinw0);

        var norm = 1.0 + alpha / a;
        _a0 = (1.0 + alpha * a) / norm;
        _a1 = (-2.0 * cosw0) / norm;
        _a2 = (1.0 - alpha * a) / norm;
        _b1 = (-2.0 * cosw0) / norm;
        _b2 = (1.0 - alpha / a) / norm;
        _enabled = true;
    }

    public double Process(double input)
    {
        if (!_enabled)
            return input;

        var output = _a0 * input + _z1;
        _z1 = _a1 * input - _b1 * output + _z2;
        _z2 = _a2 * input - _b2 * output;
        return output;
    }
}
