using System;

namespace MidiSharp.Synth;

/// <summary>
/// ADSR envelope stage.
/// </summary>
public enum EnvelopeStage
{
    Idle,
    Delay,
    Attack,
    Hold,
    Decay,
    Sustain,
    Release
}

/// <summary>
/// ADSR envelope generator for volume and modulation.
/// SF2 envelopes use timecents (-12000 to 8000) where 0 = 1 second.
/// </summary>
public sealed class Envelope
{
    // Per SF2 spec §8.1.3: decay and release segments are linear in centibels of the
    // volume signal — equivalently, exponential in linear amplitude. -100 dB is the
    // canonical "essentially silent" floor the spec defines release time against.
    private const double SilenceFloor = 1e-5; // amplitude == -100 dB

    private EnvelopeStage _stage = EnvelopeStage.Idle;
    private double _currentLevel;
    private double _targetLevel;
    private double _increment;       // additive (linear stages: attack)
    private double _decayFactor;     // multiplicative per sample, < 1
    private double _releaseFactor;   // multiplicative per sample, < 1
    private int _samplesRemaining;
    private readonly int _sampleRate;

    // SF2 envelope parameters (in timecents, converted to samples)
    private int _delaySamples;
    private int _attackSamples;
    private int _holdSamples;
    private int _decaySamples;
    private double _sustainLevel; // 0.0 to 1.0 (from centibels)
    private int _releaseSamples;

    // Key-to-envelope modulation
    private double _keynumToHold;
    private double _keynumToDecay;
    private int _keyNumber;

    /// <summary>
    /// Current envelope value (0.0 to 1.0).
    /// </summary>
    public double Value => _currentLevel;

    /// <summary>
    /// Current envelope stage.
    /// </summary>
    public EnvelopeStage Stage => _stage;

    /// <summary>
    /// Whether the envelope has completed (reached idle after release).
    /// </summary>
    public bool IsFinished => _stage == EnvelopeStage.Idle && _currentLevel <= SilenceFloor;

    /// <summary>
    /// Creates a new envelope with default parameters.
    /// </summary>
    public Envelope(int sampleRate)
    {
        _sampleRate = sampleRate;
        Reset();
    }

    /// <summary>
    /// Resets the envelope to idle state.
    /// </summary>
    public void Reset()
    {
        _stage = EnvelopeStage.Idle;
        _currentLevel = 0;
        _targetLevel = 0;
        _increment = 0;
        _samplesRemaining = 0;
    }

    /// <summary>
    /// Sets envelope parameters from SF2 generator values.
    /// </summary>
    /// <param name="delayTimecents">Delay time in timecents (-12000 to 5000)</param>
    /// <param name="attackTimecents">Attack time in timecents (-12000 to 8000)</param>
    /// <param name="holdTimecents">Hold time in timecents (-12000 to 5000)</param>
    /// <param name="decayTimecents">Decay time in timecents (-12000 to 8000)</param>
    /// <param name="sustainCentibels">Sustain level in centibels (0 to 1440, 0=full, 1440=silence)</param>
    /// <param name="releaseTimecents">Release time in timecents (-12000 to 8000)</param>
    /// <param name="keynumToHold">Key number to hold time modulation (timecents/key)</param>
    /// <param name="keynumToDecay">Key number to decay time modulation (timecents/key)</param>
    public void SetParameters(
        short delayTimecents,
        short attackTimecents,
        short holdTimecents,
        short decayTimecents,
        short sustainCentibels,
        short releaseTimecents,
        short keynumToHold = 0,
        short keynumToDecay = 0)
    {
        _delaySamples = TimecentsToSamples(delayTimecents);
        _attackSamples = TimecentsToSamples(attackTimecents);
        _holdSamples = TimecentsToSamples(holdTimecents);
        _decaySamples = TimecentsToSamples(decayTimecents);
        _sustainLevel = CentibelsToLinear(sustainCentibels);
        _releaseSamples = TimecentsToSamples(releaseTimecents);
        _keynumToHold = keynumToHold;
        _keynumToDecay = keynumToDecay;
    }

    /// <summary>
    /// Sets default volume envelope parameters.
    /// </summary>
    public void SetDefaultVolume()
    {
        // SF2 defaults: instant attack, no decay, full sustain, ~1s release
        _delaySamples = 0;
        _attackSamples = 1;
        _holdSamples = 0;
        _decaySamples = 0;
        _sustainLevel = 1.0;
        _releaseSamples = TimecentsToSamples(0); // ~1 second
    }

    /// <summary>
    /// Sets default modulation envelope parameters.
    /// </summary>
    public void SetDefaultModulation()
    {
        _delaySamples = 0;
        _attackSamples = 1;
        _holdSamples = 0;
        _decaySamples = 0;
        _sustainLevel = 1.0;
        _releaseSamples = TimecentsToSamples(0);
    }

    /// <summary>
    /// Triggers the envelope (note on).
    /// </summary>
    public void Trigger(int keyNumber = 60)
    {
        _keyNumber = keyNumber;
        _currentLevel = 0;

        if (_delaySamples > 0)
        {
            _stage = EnvelopeStage.Delay;
            _samplesRemaining = _delaySamples;
            _targetLevel = 0;
            _increment = 0;
        }
        else
        {
            StartAttack();
        }
    }

    /// <summary>
    /// Releases the envelope (note off).
    /// </summary>
    public void Release()
    {
        if (_stage == EnvelopeStage.Idle)
            return;

        _stage = EnvelopeStage.Release;
        _targetLevel = 0;
        _samplesRemaining = _releaseSamples;

        // SF2 spec: release time is the time from peak to -100 dB. Multiplicative
        // per-sample factor that drops the signal by 100 dB over _releaseSamples
        // steps, regardless of where in the envelope we started (decay/sustain).
        if (_samplesRemaining > 0)
            _releaseFactor = Math.Pow(10.0, -100.0 / 20.0 / _samplesRemaining);
        else
            _releaseFactor = 0;
    }

    /// <summary>
    /// Processes one sample and returns the envelope value.
    /// </summary>
    public double Process()
    {
        switch (_stage)
        {
            case EnvelopeStage.Delay:
                if (--_samplesRemaining <= 0)
                    StartAttack();
                break;

            case EnvelopeStage.Attack:
                _currentLevel += _increment;
                if (_currentLevel >= 1.0)
                {
                    _currentLevel = 1.0;
                    StartHold();
                }
                break;

            case EnvelopeStage.Hold:
                if (--_samplesRemaining <= 0)
                    StartDecay();
                break;

            case EnvelopeStage.Decay:
                _currentLevel *= _decayFactor;
                if (_currentLevel <= _sustainLevel)
                {
                    _currentLevel = _sustainLevel;
                    _stage = EnvelopeStage.Sustain;
                }
                break;

            case EnvelopeStage.Sustain:
                // Stay at sustain level until release
                break;

            case EnvelopeStage.Release:
                _currentLevel *= _releaseFactor;
                if (_currentLevel <= SilenceFloor)
                {
                    _currentLevel = 0;
                    _stage = EnvelopeStage.Idle;
                }
                break;
        }

        return _currentLevel;
    }

    private void StartAttack()
    {
        _stage = EnvelopeStage.Attack;
        _targetLevel = 1.0;
        _samplesRemaining = _attackSamples;

        if (_samplesRemaining > 0)
            _increment = (1.0 - _currentLevel) / _samplesRemaining;
        else
        {
            _currentLevel = 1.0;
            StartHold();
        }
    }

    private void StartHold()
    {
        // Apply key-to-hold modulation
        var holdSamples = _holdSamples;
        if (_keynumToHold != 0)
        {
            var modulation = _keynumToHold * (60 - _keyNumber);
            holdSamples = TimecentsToSamples((short)((short)modulation + SamplesToTimecents(holdSamples)));
        }

        if (holdSamples > 0)
        {
            _stage = EnvelopeStage.Hold;
            _samplesRemaining = holdSamples;
        }
        else
        {
            StartDecay();
        }
    }

    private void StartDecay()
    {
        // Apply key-to-decay modulation
        var decaySamples = _decaySamples;
        if (_keynumToDecay != 0)
        {
            var modulation = _keynumToDecay * (60 - _keyNumber);
            decaySamples = TimecentsToSamples((short)((short)modulation + SamplesToTimecents(decaySamples)));
        }

        if (decaySamples > 0 && _sustainLevel < 1.0)
        {
            _stage = EnvelopeStage.Decay;
            _targetLevel = _sustainLevel;
            _samplesRemaining = decaySamples;

            // Decay from peak (1.0) down to sustainLevel as a dB-linear ramp.
            // Per-sample multiplicative factor: factor^decaySamples == sustainLevel.
            var target = Math.Max(_sustainLevel, SilenceFloor);
            _decayFactor = Math.Pow(target, 1.0 / decaySamples);
        }
        else
        {
            _currentLevel = _sustainLevel;
            _stage = EnvelopeStage.Sustain;
        }
    }

    /// <summary>
    /// Converts timecents to samples.
    /// Timecents: 0 = 1 second, 1200 = 2 seconds, -1200 = 0.5 seconds
    /// </summary>
    private int TimecentsToSamples(short timecents)
    {
        if (timecents <= -12000)
            return 0;

        var seconds = Math.Pow(2.0, timecents / 1200.0);
        return Math.Max(1, (int)(seconds * _sampleRate));
    }

    private short SamplesToTimecents(int samples)
    {
        if (samples <= 0)
            return -12000;

        var seconds = (double)samples / _sampleRate;
        return (short)(1200.0 * Math.Log(seconds) / Math.Log(2.0));
    }

    /// <summary>
    /// Converts centibels to linear amplitude (0.0 to 1.0).
    /// 0 cb = 1.0, 1440 cb = 0.0 (silence)
    /// </summary>
    private static double CentibelsToLinear(short centibels)
    {
        if (centibels >= 1440)
            return 0.0;
        if (centibels <= 0)
            return 1.0;

        // centibels to dB: cb / 10
        // dB to linear: 10^(dB/20)
        return Math.Pow(10.0, -centibels / 200.0);
    }
}
