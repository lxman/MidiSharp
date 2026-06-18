using System;
using System.Collections.Generic;
using MidiSharp.SoundBank;

namespace MidiSharp.Synth;

/// <summary>
/// Waveform table for the SFZ v2 generic LFO (<c>lfoN_wave</c>). Evaluates a normalized phase
/// (any real; wrapped to 0..1) to a -1..1 value. Numbering matches the SFZ v2 / ARIA spec.
/// </summary>
internal static class GenericLfoWave
{
    public static double Eval(int wave, double phase)
    {
        double p = phase - Math.Floor(phase);   // wrap to [0,1)
        switch (wave)
        {
            case 0:  // triangle — starts at 0 rising, +1 at 1/4, -1 at 3/4
                if (p < 0.25) return 4.0 * p;
                if (p < 0.75) return 2.0 - 4.0 * p;
                return 4.0 * p - 4.0;
            case 1:  return Math.Sin(2.0 * Math.PI * p);   // sine (v2 default)
            case 2:  return p < 0.75 ? 1.0 : -1.0;         // 75% pulse
            case 3:  return p < 0.5 ? 1.0 : -1.0;          // square (50% pulse)
            case 4:  return p < 0.25 ? 1.0 : -1.0;         // 25% pulse
            case 5:  return p < 0.125 ? 1.0 : -1.0;        // 12.5% pulse
            case 6:  return 2.0 * p - 1.0;                 // saw up
            case 7:  return 1.0 - 2.0 * p;                 // saw down
            // 12 (random S&H) and 13 (stepped) are stateful/table-driven, handled in the runner's
            // StageSum, not here. Any other unknown wave falls back to sine.
            default: return Math.Sin(2.0 * Math.PI * p);
        }
    }
}

/// <summary>
/// Per-voice runtime for one SFZ v2 generic LFO. Per sample it advances the oscillator (delay → linear
/// fade-in → full depth); per block <see cref="BeginBlock"/> recomputes the frequency and the
/// per-destination depths from the live controller values (lfoN_freq_onccN and the
/// lfoN_{target}_onccN mod-wheel-vibrato depths), plus the EQ-band modulation deltas for the block.
/// Reused across notes to avoid per-NoteOn allocation.
/// </summary>
internal sealed class GenericLfoRunner
{
    private int _sampleRate = 48000;
    private double _phase;          // cycles, 0..1
    private long _cycle;            // completed periods since the note started (for sample-and-hold)
    private double _phaseInc;       // cycles per sample (set per block)
    private int _delaySamples, _delayCounter, _fadeSamples, _fadeElapsed;
    private LfoStage[] _stages = [];

    // Static config (base value + CC modulations), set at Configure.
    private double _baseFreqHz;
    private LfoCcDepth[]? _freqCc;
    private double _basePitch, _baseVolume, _baseCutoff;
    private LfoCcDepth[]? _pitchCc, _volumeCc, _cutoffCc;

    // EQ targets: band number, whether it modulates freq (else gain), base depth + CC depth.
    private int _eqCount;
    private int[] _eqBand = [];
    private bool[] _eqIsFreq = [];
    private double[] _eqBaseDepth = [];
    private LfoCcDepth[]?[] _eqDepthCc = [];
    private double[] _eqDelta = [];   // computed per block = peek × effective depth

    // Effective per-block depths the voice reads while iterating samples.
    public double PitchDepthCents { get; private set; }
    public double VolumeDepthDb { get; private set; }
    public double CutoffDepthCents { get; private set; }

    public int EqCount => _eqCount;
    public int EqTargetBand(int i) => _eqBand[i];
    public bool EqTargetIsFreq(int i) => _eqIsFreq[i];
    public double EqTargetDelta(int i) => _eqDelta[i];   // gain dB, or freq Hz, delta for this block

    public void Configure(GenericLfo lfo, int sampleRate)
    {
        _sampleRate = sampleRate;
        _phase = lfo.Phase - Math.Floor(lfo.Phase);
        _cycle = 0;
        _delaySamples = lfo.DelaySeconds > 0 ? (int)(lfo.DelaySeconds * sampleRate) : 0;
        _fadeSamples = lfo.FadeSeconds > 0 ? (int)(lfo.FadeSeconds * sampleRate) : 0;
        _delayCounter = _delaySamples;
        _fadeElapsed = 0;
        _stages = lfo.Stages;

        _baseFreqHz = lfo.FrequencyHz;
        _freqCc = lfo.FreqCc;
        _basePitch = _baseVolume = _baseCutoff = 0;
        _pitchCc = _volumeCc = _cutoffCc = null;

        int eqNeeded = 0;
        foreach (var t in lfo.Targets)
            if (t.Destination is LfoDestination.EqGain or LfoDestination.EqFreq) eqNeeded++;
        if (_eqBand.Length < eqNeeded)
        {
            _eqBand = new int[eqNeeded];
            _eqIsFreq = new bool[eqNeeded];
            _eqBaseDepth = new double[eqNeeded];
            _eqDepthCc = new LfoCcDepth[eqNeeded][];
            _eqDelta = new double[eqNeeded];
        }
        _eqCount = 0;

        foreach (var t in lfo.Targets)
        {
            switch (t.Destination)
            {
                case LfoDestination.Pitch:  _basePitch += t.Depth;  _pitchCc = Merge(_pitchCc, t.DepthCc); break;
                case LfoDestination.Volume: _baseVolume += t.Depth; _volumeCc = Merge(_volumeCc, t.DepthCc); break;
                case LfoDestination.Cutoff: _baseCutoff += t.Depth; _cutoffCc = Merge(_cutoffCc, t.DepthCc); break;
                case LfoDestination.EqGain:
                case LfoDestination.EqFreq:
                    _eqBand[_eqCount] = t.EqBand;
                    _eqIsFreq[_eqCount] = t.Destination == LfoDestination.EqFreq;
                    _eqBaseDepth[_eqCount] = t.Depth;
                    _eqDepthCc[_eqCount] = t.DepthCc;
                    _eqCount++;
                    break;
            }
        }

        // Seed effective values with the no-CC case; BeginBlock refines them each block.
        _phaseInc = _baseFreqHz / sampleRate;
        PitchDepthCents = _basePitch;
        VolumeDepthDb = _baseVolume;
        CutoffDepthCents = _baseCutoff;
        for (int i = 0; i < _eqCount; i++) _eqDelta[i] = 0;
    }

    /// <summary>Recomputes frequency, target depths and EQ deltas from the channel's current CCs.</summary>
    public void BeginBlock(ChannelState ch)
    {
        _phaseInc = (_baseFreqHz + SumCc(_freqCc, ch)) / _sampleRate;
        PitchDepthCents = _basePitch + SumCc(_pitchCc, ch);
        VolumeDepthDb = _baseVolume + SumCc(_volumeCc, ch);
        CutoffDepthCents = _baseCutoff + SumCc(_cutoffCc, ch);

        if (_eqCount > 0)
        {
            double peek = Peek();
            for (int i = 0; i < _eqCount; i++)
                _eqDelta[i] = peek * (_eqBaseDepth[i] + SumCc(_eqDepthCc[i], ch));
        }
    }

    /// <summary>Advances one sample and returns the LFO value (-1..1, with delay/fade applied).</summary>
    public double Process()
    {
        if (_delayCounter > 0) { _delayCounter--; return 0.0; }

        double v = StageSum();
        if (_fadeSamples > 0 && _fadeElapsed < _fadeSamples)
        {
            v *= (double)_fadeElapsed / _fadeSamples;
            _fadeElapsed++;
        }

        _phase += _phaseInc;
        if (_phase >= 1.0) { double f = Math.Floor(_phase); _phase -= f; _cycle += (long)f; }
        return v;
    }

    /// <summary>The LFO value at the current phase without advancing — used for per-block EQ modulation.</summary>
    private double Peek()
    {
        if (_delayCounter > 0) return 0.0;
        double v = StageSum();
        if (_fadeSamples > 0 && _fadeElapsed < _fadeSamples)
            v *= (double)_fadeElapsed / _fadeSamples;
        return v;
    }

    private double StageSum()
    {
        double v = 0.0;
        for (int s = 0; s < _stages.Length; s++)
        {
            var st = _stages[s];
            double w = st.Wave switch
            {
                // Random sample-and-hold: a new random value twice per period, held between. Deterministic
                // (hashed from the monotonic half-period index) so renders stay reproducible.
                12 => SampleHold((_cycle + _phase) * st.Ratio),
                // Stepped staircase: walk the step table evenly across the period (frac handles sub-stage
                // ratios). With no steps defined it contributes nothing.
                13 => Stepped(st.Steps, _phase * st.Ratio),
                _ => GenericLfoWave.Eval(st.Wave, _phase * st.Ratio),
            };
            v += w * st.Scale + st.Offset;
        }
        return v;
    }

    /// <summary>Random value in [-1,1) for the half-period the (monotonic) phase falls in, sampled twice
    /// per cycle and held — derived from a splitmix64 hash of the half index, so it's reproducible.</summary>
    private static double SampleHold(double monotonicPhase)
    {
        long halfIndex = (long)Math.Floor(monotonicPhase * 2.0);
        ulong x = (ulong)halfIndex + 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (x >> 11) * (1.0 / (1UL << 53)) * 2.0 - 1.0;
    }

    /// <summary>Stepped-LFO value: the step the wrapped phase lands in (each step occupies 1/N of a period).</summary>
    private static double Stepped(double[]? steps, double phase)
    {
        if (steps is not { Length: > 0 }) return 0.0;
        double p = phase - Math.Floor(phase);
        int i = (int)(p * steps.Length);
        if (i >= steps.Length) i = steps.Length - 1;   // guard the p≈1 boundary
        return steps[i];
    }

    private static double SumCc(LfoCcDepth[]? mods, ChannelState ch)
    {
        if (mods == null) return 0.0;
        double sum = 0.0;
        for (int i = 0; i < mods.Length; i++)
            sum += ch.GetCC(mods[i].Cc) / 127.0 * mods[i].Amount;
        return sum;
    }

    private static LfoCcDepth[]? Merge(LfoCcDepth[]? a, LfoCcDepth[]? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        var list = new List<LfoCcDepth>(a);
        list.AddRange(b);
        return list.ToArray();
    }
}
