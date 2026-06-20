using System;
using System.Collections.Generic;

namespace MidiSharp.Dsp;

/// <summary>One band of a <see cref="ParametricEq"/>: a response type, centre/corner frequency,
/// Q (bandwidth) and gain. Gain is ignored for the pass/notch types.</summary>
public readonly record struct EqBandSpec(BiquadType Type, double FrequencyHz, double Q, double GainDb)
{
    /// <summary>A neutral peaking band at 1 kHz (0 dB = inaudible), handy as a UI default.</summary>
    public static EqBandSpec Peak(double freqHz, double q, double gainDb)
        => new(BiquadType.Peaking, freqHz, q, gainDb);

    public static EqBandSpec LowShelf(double freqHz, double gainDb, double q = 0.707)
        => new(BiquadType.LowShelf, freqHz, q, gainDb);

    public static EqBandSpec HighShelf(double freqHz, double gainDb, double q = 0.707)
        => new(BiquadType.HighShelf, freqHz, q, gainDb);
}

/// <summary>
/// A master-bus parametric equalizer: a cascade of <see cref="BiquadFilter"/> bands applied to an
/// interleaved-stereo block, with independent left/right filter state. Reconfigure live via
/// <see cref="SetBands"/> (coefficients recompute, running state is preserved for a smooth change);
/// an empty band list is a pass-through. This is the first concrete processor on the master seam —
/// per-instrument EQ later reuses the same class on a per-instrument bus.
/// </summary>
public sealed class ParametricEq : IAudioProcessor
{
    private readonly int _sampleRate;
    private BiquadFilter[] _left = [];
    private BiquadFilter[] _right = [];
    private int _count;

    public ParametricEq(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
    }

    /// <summary>Number of active bands.</summary>
    public int BandCount => _count;

    /// <summary>
    /// (Re)configures the band list. The filter-instance pool grows as needed and is reused across
    /// calls, so live edits don't allocate once the pool is large enough. Coefficients are recomputed;
    /// existing per-band delay state carries over (a click-free parameter change).
    /// </summary>
    public void SetBands(IReadOnlyList<EqBandSpec> bands)
    {
        var n = bands?.Count ?? 0;
        if (_left.Length < n)
        {
            var newLeft = new BiquadFilter[n];
            var newRight = new BiquadFilter[n];
            Array.Copy(_left, newLeft, _left.Length);
            Array.Copy(_right, newRight, _right.Length);
            for (var i = _left.Length; i < n; i++)
            {
                newLeft[i] = new BiquadFilter(_sampleRate);
                newRight[i] = new BiquadFilter(_sampleRate);
            }
            _left = newLeft;
            _right = newRight;
        }

        for (var i = 0; i < n; i++)
        {
            var b = bands![i];
            _left[i].Configure(b.Type, b.FrequencyHz, b.Q, b.GainDb);
            _right[i].Configure(b.Type, b.FrequencyHz, b.Q, b.GainDb);
        }
        _count = n;
    }

    public void Process(Span<float> interleavedStereo)
    {
        if (_count == 0) return;
        var frames = interleavedStereo.Length / 2;
        for (var f = 0; f < frames; f++)
        {
            int li = f * 2, ri = li + 1;
            double l = interleavedStereo[li];
            double r = interleavedStereo[ri];
            for (var b = 0; b < _count; b++)
            {
                l = _left[b].Process(l);
                r = _right[b].Process(r);
            }
            interleavedStereo[li] = (float)l;
            interleavedStereo[ri] = (float)r;
        }
    }

    public void Reset()
    {
        for (var i = 0; i < _count; i++)
        {
            _left[i].Reset();
            _right[i].Reset();
        }
    }
}
