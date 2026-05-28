using System;

namespace MidiSharp.Synth;

/// <summary>
/// Low Frequency Oscillator for vibrato and tremolo effects.
/// SF2 uses timecents for delay and frequency in milli-Hz (0 = 8.176 Hz).
/// </summary>
public sealed class LowFrequencyOscillator
{
    private double _phase;
    private double _phaseIncrement;
    private int _delaySamples;
    private int _delayCounter;
    private readonly int _sampleRate;
    private double _frequency;

    /// <summary>
    /// Current LFO output value (-1.0 to 1.0).
    /// </summary>
    public double Value { get; private set; }

    /// <summary>
    /// Whether the LFO delay period has completed.
    /// </summary>
    public bool IsActive => _delayCounter <= 0;

    /// <summary>
    /// Creates a new LFO.
    /// </summary>
    public LowFrequencyOscillator(int sampleRate)
    {
        _sampleRate = sampleRate;
        Reset();
    }

    /// <summary>
    /// Resets the LFO to its initial state.
    /// </summary>
    public void Reset()
    {
        _phase = 0;
        _delayCounter = _delaySamples;
        Value = 0;
    }

    /// <summary>
    /// Sets LFO parameters from SF2 generator values.
    /// </summary>
    /// <param name="delayTimecents">Delay before LFO starts (-12000 to 5000 timecents)</param>
    /// <param name="freqCents">Frequency in absolute cents (0 = 8.176 Hz)</param>
    public void SetParameters(short delayTimecents, short freqCents)
    {
        _delaySamples = TimecentsToSamples(delayTimecents);
        _frequency = AbsoluteCentsToFrequency(freqCents);
        _phaseIncrement = 2.0 * Math.PI * _frequency / _sampleRate;
        _delayCounter = _delaySamples;
    }

    /// <summary>
    /// Sets default modulation LFO parameters.
    /// </summary>
    public void SetDefaultModLfo()
    {
        // Default: no delay, ~8 Hz
        _delaySamples = 0;
        _frequency = 8.176;
        _phaseIncrement = 2.0 * Math.PI * _frequency / _sampleRate;
        _delayCounter = 0;
    }

    /// <summary>
    /// Sets default vibrato LFO parameters.
    /// </summary>
    public void SetDefaultVibLfo()
    {
        // Default: no delay, ~8 Hz
        _delaySamples = 0;
        _frequency = 8.176;
        _phaseIncrement = 2.0 * Math.PI * _frequency / _sampleRate;
        _delayCounter = 0;
    }

    /// <summary>
    /// Triggers the LFO (resets delay counter).
    /// </summary>
    public void Trigger()
    {
        _phase = 0;
        _delayCounter = _delaySamples;
        Value = 0;
    }

    /// <summary>
    /// Processes one sample and returns the LFO value.
    /// </summary>
    public double Process()
    {
        if (_delayCounter > 0)
        {
            _delayCounter--;
            Value = 0;
            return 0;
        }

        // Triangle wave LFO (standard for SF2)
        // Convert sine phase to triangle: 4 * |((phase/2π + 0.25) % 1) - 0.5| - 1
        var normalizedPhase = (_phase / (2.0 * Math.PI) + 0.25) % 1.0;
        Value = 4.0 * Math.Abs(normalizedPhase - 0.5) - 1.0;

        // Advance phase
        _phase += _phaseIncrement;
        if (_phase >= 2.0 * Math.PI)
            _phase -= 2.0 * Math.PI;

        return Value;
    }

    /// <summary>
    /// Converts timecents to samples.
    /// </summary>
    private int TimecentsToSamples(short timecents)
    {
        if (timecents <= -12000)
            return 0;

        var seconds = Math.Pow(2.0, timecents / 1200.0);
        return (int)(seconds * _sampleRate);
    }

    /// <summary>
    /// Converts absolute cents to frequency in Hz.
    /// 0 cents = 8.176 Hz (MIDI note 0)
    /// </summary>
    private static double AbsoluteCentsToFrequency(short cents)
    {
        // 8.176 Hz * 2^(cents/1200)
        return 8.176 * Math.Pow(2.0, cents / 1200.0);
    }
}
