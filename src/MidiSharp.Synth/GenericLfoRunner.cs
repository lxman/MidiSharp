using System;
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
            default: return Math.Sin(2.0 * Math.PI * p);   // 12 (S&H) / 13 (stepped) land in Phase 3
        }
    }
}

/// <summary>
/// Per-voice runtime for one SFZ v2 generic LFO: advances an oscillator each sample (delay, then a
/// linear fade-in, then full depth) and exposes the per-destination depths so the voice can fold the
/// LFO's value into the per-sample pitch / attenuation / cutoff. Reused across notes via
/// <see cref="Configure"/> to avoid per-NoteOn allocation.
/// </summary>
internal sealed class GenericLfoRunner
{
    private double _phase;          // cycles, 0..1
    private double _phaseInc;       // cycles per sample
    private int _delaySamples, _delayCounter, _fadeSamples, _fadeElapsed;
    private LfoStage[] _stages = [];

    // Summed depths across this LFO's targets, in the destination's natural units.
    public double PitchDepthCents { get; private set; }
    public double VolumeDepthDb { get; private set; }
    public double CutoffDepthCents { get; private set; }

    public void Configure(GenericLfo lfo, int sampleRate)
    {
        _phase = lfo.Phase - Math.Floor(lfo.Phase);
        _phaseInc = lfo.FrequencyHz / sampleRate;
        _delaySamples = lfo.DelaySeconds > 0 ? (int)(lfo.DelaySeconds * sampleRate) : 0;
        _fadeSamples = lfo.FadeSeconds > 0 ? (int)(lfo.FadeSeconds * sampleRate) : 0;
        _delayCounter = _delaySamples;
        _fadeElapsed = 0;
        _stages = lfo.Stages;

        PitchDepthCents = 0;
        VolumeDepthDb = 0;
        CutoffDepthCents = 0;
        foreach (var t in lfo.Targets)
        {
            switch (t.Destination)
            {
                case LfoDestination.Pitch:  PitchDepthCents += t.Depth; break;
                case LfoDestination.Volume: VolumeDepthDb += t.Depth; break;
                case LfoDestination.Cutoff: CutoffDepthCents += t.Depth; break;
            }
        }
    }

    /// <summary>Advances one sample and returns the LFO value (-1..1, with delay/fade applied).</summary>
    public double Process()
    {
        if (_delayCounter > 0) { _delayCounter--; return 0.0; }

        double v = 0.0;
        for (int s = 0; s < _stages.Length; s++)
        {
            var st = _stages[s];
            v += GenericLfoWave.Eval(st.Wave, _phase * st.Ratio) * st.Scale + st.Offset;
        }

        if (_fadeSamples > 0 && _fadeElapsed < _fadeSamples)
        {
            v *= (double)_fadeElapsed / _fadeSamples;
            _fadeElapsed++;
        }

        _phase += _phaseInc;
        if (_phase >= 1.0) _phase -= Math.Floor(_phase);
        return v;
    }
}
