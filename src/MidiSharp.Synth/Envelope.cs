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

    // ARIA ampeg_release_shape: when non-zero the release follows a power-shaped linear-amplitude ramp
    // instead of the dB-linear exponential. _releaseShapeExp is the precomputed (1-p) exponent; 0 keeps
    // the exponential path (so SF2 and non-shaped SFZ are unchanged).
    private double _releaseShape;
    private double _releaseShapeExp = 1.0;
    private double _releaseStartLevel;
    private int _releaseElapsed;

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
        _releaseElapsed = 0;
        _releaseStartLevel = 0;
    }

    /// <summary>
    /// Sets envelope parameters from SF2 generator values. Delegates to the
    /// domain-typed overload after converting timecents → seconds and centibels
    /// → linear amplitude.
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
        SetParameters(
            delaySeconds: TimecentsToSeconds(delayTimecents),
            attackSeconds: TimecentsToSeconds(attackTimecents),
            holdSeconds: TimecentsToSeconds(holdTimecents),
            decaySeconds: TimecentsToSeconds(decayTimecents),
            sustainLevel: CentibelsToLinear(sustainCentibels),
            releaseSeconds: TimecentsToSeconds(releaseTimecents),
            keynumToHoldCentsPerKey: keynumToHold,
            keynumToDecayCentsPerKey: keynumToDecay);
    }

    /// <summary>
    /// Sets envelope parameters in domain-natural units. This is the preferred
    /// entry point — SF2's timecent / centibel encodings should be converted
    /// at the boundary (loader) rather than carried into the synth.
    /// </summary>
    /// <param name="delaySeconds">Delay before attack starts. 0 = no delay phase.</param>
    /// <param name="attackSeconds">Attack time (0 → peak). 0 = instantaneous attack.</param>
    /// <param name="holdSeconds">Time held at peak before decay begins.</param>
    /// <param name="decaySeconds">Decay time (peak → sustain). 0 = jump directly to sustain.</param>
    /// <param name="sustainLevel">Sustain level as a 0..1 linear amplitude multiplier.</param>
    /// <param name="releaseSeconds">Release time (current → 0). 0 = instant cutoff.</param>
    /// <param name="keynumToHoldCentsPerKey">Cents of hold-time offset per key away from middle C.</param>
    /// <param name="keynumToDecayCentsPerKey">Cents of decay-time offset per key away from middle C.</param>
    public void SetParameters(
        double delaySeconds,
        double attackSeconds,
        double holdSeconds,
        double decaySeconds,
        double sustainLevel,
        double releaseSeconds,
        double keynumToHoldCentsPerKey = 0,
        double keynumToDecayCentsPerKey = 0)
    {
        _delaySamples = SecondsToSamples(delaySeconds);
        _attackSamples = SecondsToSamples(attackSeconds);
        _holdSamples = SecondsToSamples(holdSeconds);
        _decaySamples = SecondsToSamples(decaySeconds);
        _sustainLevel = sustainLevel;
        _releaseSamples = SecondsToSamples(releaseSeconds);
        _keynumToHold = keynumToHoldCentsPerKey;
        _keynumToDecay = keynumToDecayCentsPerKey;
    }

    private int SecondsToSamples(double seconds)
    {
        if (seconds <= 0) return 0;
        return Math.Max(1, (int)(seconds * _sampleRate));
    }

    /// <summary>
    /// Overrides just the release time (current → 0). Used for SFZ off_mode/off_time, where a voice
    /// turned off by a retrigger or off_by group fades over a specific time rather than its own release.
    /// </summary>
    public void SetReleaseTime(double seconds) => _releaseSamples = SecondsToSamples(seconds);

    /// <summary>
    /// Sets the ARIA ampeg_release_shape curvature. 0 (default) keeps the dB-linear exponential release;
    /// negative makes it more convex (fast initial drop, long tail — natural piano damping), positive
    /// more concave. Mapped to a (1-p) power on a linear-amplitude ramp: k = 2^(-shape/6).
    /// </summary>
    public void SetReleaseShape(double shape)
    {
        _releaseShape = shape;
        _releaseShapeExp = shape == 0 ? 1.0 : Math.Pow(2.0, -shape / 6.0);
    }

    /// <summary>
    /// Scales the attack, decay, and release stage times in place by powers
    /// of two (octaves). Used by Synthesizer for GM2 sound-controller CCs
    /// 72/73/75 which adjust per-channel envelope times. Hold and delay
    /// are unaffected.
    /// </summary>
    public void ScaleStageTimes(double attackOctaves, double decayOctaves, double releaseOctaves)
    {
        if (attackOctaves != 0) _attackSamples = ScaleSamples(_attackSamples, attackOctaves);
        if (decayOctaves != 0) _decaySamples = ScaleSamples(_decaySamples, decayOctaves);
        if (releaseOctaves != 0) _releaseSamples = ScaleSamples(_releaseSamples, releaseOctaves);
    }

    private int ScaleSamples(int samples, double octaves)
    {
        if (samples <= 0) return 0;
        double scaled = samples * Math.Pow(2.0, octaves);
        if (scaled <= 0) return 0;
        if (scaled >= int.MaxValue) return int.MaxValue;
        return Math.Max(1, (int)scaled);
    }

    private static double TimecentsToSeconds(short timecents)
    {
        if (timecents <= -12000) return 0;
        return Math.Pow(2.0, timecents / 1200.0);
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
        // Shaped-release bookkeeping: capture the level we release from and reset the ramp clock.
        _releaseStartLevel = _currentLevel;
        _releaseElapsed = 0;

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
                if (_releaseShape != 0 && _releaseSamples > 0)
                {
                    // ARIA shaped release: level = startLevel · (1−p)^k over the release window.
                    _releaseElapsed++;
                    double p = (double)_releaseElapsed / _releaseSamples;
                    _currentLevel = p >= 1.0 ? 0.0 : _releaseStartLevel * Math.Pow(1.0 - p, _releaseShapeExp);
                }
                else
                {
                    _currentLevel *= _releaseFactor;
                }
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
