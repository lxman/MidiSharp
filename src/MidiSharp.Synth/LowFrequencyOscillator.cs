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
    private int _fadeSamples;
    private int _fadeElapsed;
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
        _fadeElapsed = 0;
        Value = 0;
    }

    /// <summary>
    /// Sets LFO parameters from SF2 generator values. Delegates to the
    /// domain-typed overload after converting timecents → seconds and
    /// absolute cents → Hz.
    /// </summary>
    /// <param name="delayTimecents">Delay before LFO starts (-12000 to 5000 timecents)</param>
    /// <param name="freqCents">Frequency in absolute cents (0 = 8.176 Hz)</param>
    public void SetParameters(short delayTimecents, short freqCents)
    {
        SetParameters(
            delaySeconds: TimecentsToSeconds(delayTimecents),
            frequencyHz: AbsoluteCentsToFrequency(freqCents));
    }

    /// <summary>
    /// Sets LFO parameters in domain-natural units. Preferred over the
    /// SF2-unit overload going forward; the loader should convert at the
    /// boundary rather than carrying timecents/abs-cents into the synth.
    /// </summary>
    /// <param name="delaySeconds">Delay before oscillation starts. 0 = no delay.</param>
    /// <param name="frequencyHz">Oscillation frequency in Hz.</param>
    /// <param name="fadeSeconds">Linear depth fade-in after the delay. 0 = full depth at once.</param>
    public void SetParameters(double delaySeconds, double frequencyHz, double fadeSeconds = 0)
    {
        _delaySamples = delaySeconds <= 0 ? 0 : (int)(delaySeconds * _sampleRate);
        _fadeSamples = fadeSeconds <= 0 ? 0 : (int)(fadeSeconds * _sampleRate);
        _frequency = frequencyHz;
        _phaseIncrement = 2.0 * Math.PI * frequencyHz / _sampleRate;
        _delayCounter = _delaySamples;
        _fadeElapsed = 0;
    }

    /// <summary>Updates only the oscillation frequency (e.g. SFZ pitchlfo_freq_oncc), keeping phase/delay.</summary>
    public void SetFrequency(double frequencyHz)
    {
        _frequency = frequencyHz;
        _phaseIncrement = 2.0 * Math.PI * frequencyHz / _sampleRate;
    }

    private static double TimecentsToSeconds(short timecents)
    {
        if (timecents <= -12000) return 0;
        return Math.Pow(2.0, timecents / 1200.0);
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
        _fadeElapsed = 0;
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

        // Fade-in: ramp depth 0→1 linearly over the fade window after the delay (SFZ *_fade).
        if (_fadeSamples > 0 && _fadeElapsed < _fadeSamples)
        {
            Value *= (double)_fadeElapsed / _fadeSamples;
            _fadeElapsed++;
        }

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
