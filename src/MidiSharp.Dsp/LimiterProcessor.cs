using System;

namespace MidiSharp.Dsp;

/// <summary>
/// A stereo-linked brickwall peak limiter / headroom guard. Guarantees the output never exceeds
/// <see cref="CeilingDb"/>: gain reduction is applied instantly when a sample would overshoot (true
/// attack-free brickwall, no lookahead needed) and recovers smoothly over <see cref="ReleaseMs"/>.
/// Both channels share one gain so the stereo image is preserved. Below the ceiling it's a
/// pass-through (unity gain), so it's safe to leave inserted; it only acts on peaks.
/// </summary>
/// <remarks>
/// Built as an ordinary <see cref="IAudioProcessor"/> "plugin": the host adds it to a
/// <see cref="ProcessorChain"/> (typically last, after EQ) and toggles it with the chain's
/// <see cref="ProcessorChain.Bypass"/> or by not adding it. All parameters are live-tunable.
/// </remarks>
public sealed class LimiterProcessor : IAudioProcessor
{
    private readonly int _sampleRate;
    private double _ceilingDb = -1.0;
    private double _releaseMs = 100.0;
    private double _releaseCoef;
    private double _ceilingLinear;
    private double _gain = 1.0;     // current gain reduction state (1 = no reduction)

    public LimiterProcessor(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        Recompute();
    }

    /// <summary>When false, <see cref="Process"/> is a no-op pass-through (the limiter is in the chain
    /// but inactive). Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Output ceiling in dBFS (e.g. -1.0). The signal is guaranteed not to exceed this.</summary>
    public double CeilingDb
    {
        get => _ceilingDb;
        set { _ceilingDb = value; Recompute(); }
    }

    /// <summary>Release time in milliseconds — how quickly gain recovers toward unity after a peak.</summary>
    public double ReleaseMs
    {
        get => _releaseMs;
        set { _releaseMs = Math.Max(0.1, value); Recompute(); }
    }

    /// <summary>The current gain reduction in dB (0 = none, negative = limiting). For UI metering.</summary>
    public double GainReductionDb => _gain >= 1.0 ? 0.0 : 20.0 * Math.Log10(_gain);

    private void Recompute()
    {
        _ceilingLinear = Math.Pow(10.0, _ceilingDb / 20.0);
        double tau = _releaseMs / 1000.0;
        _releaseCoef = Math.Exp(-1.0 / (tau * _sampleRate));
    }

    public void Process(Span<float> interleavedStereo)
    {
        if (!Enabled) return;
        int frames = interleavedStereo.Length / 2;
        double ceiling = _ceilingLinear, releaseCoef = _releaseCoef, gain = _gain;
        for (int f = 0; f < frames; f++)
        {
            int li = f * 2, ri = li + 1;
            double l = interleavedStereo[li], r = interleavedStereo[ri];
            double level = Math.Max(Math.Abs(l), Math.Abs(r));

            // Gain that would put this sample exactly at the ceiling (1.0 when already under it).
            double required = level > ceiling ? ceiling / level : 1.0;

            // Instant attack: clamp down the moment a peak would overshoot. Otherwise release toward
            // unity, but never above what this sample allows — so the ceiling is never exceeded.
            gain = required < gain
                ? required
                : Math.Min(required, 1.0 - (1.0 - gain) * releaseCoef);

            interleavedStereo[li] = (float)(l * gain);
            interleavedStereo[ri] = (float)(r * gain);
        }
        _gain = gain;
    }

    /// <summary>Resets the gain-reduction state to unity (e.g. on transport seek/stop).</summary>
    public void Reset() => _gain = 1.0;
}
