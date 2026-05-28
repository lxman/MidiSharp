using System;
using SF2Net;

namespace MidiSharp.Synth;

/// <summary>
/// Voice state for tracking active/released voices.
/// </summary>
public enum VoiceState
{
    Free,
    Playing,
    Released
}

/// <summary>
/// Loop mode for sample playback.
/// </summary>
public enum LoopMode
{
    NoLoop,
    LoopContinuously,
    LoopUntilRelease
}

/// <summary>
/// Represents a single synthesizer voice that plays one sample.
/// Handles sample playback, pitch shifting, envelopes, LFOs, and filtering.
/// </summary>
public sealed class Voice
{
    private readonly int _sampleRate;

    // Sample data — normalized float in [-1, 1], shared buffer indexed by absolute frame number.
    private float[]? _sampleData;
    private uint _sampleStart;
    private uint _sampleEnd;
    private uint _loopStart;
    private uint _loopEnd;
    private uint _baseSampleRate;
    private LoopMode _loopMode;

    // Playback position (24.8 fixed point for sub-sample precision)
    private double _position;
    private double _increment;

    // Pitch
    private int _rootKey;
    private int _keyNumber;
    private int _velocity;
    private double _pitchCorrection; // cents
    private double _coarseTune; // semitones
    private double _fineTune; // cents
    private double _scaleTuning; // cents per key (default 100)

    // Envelopes
    private readonly Envelope _volumeEnvelope;
    private readonly Envelope _modulationEnvelope;

    // LFOs
    private readonly LowFrequencyOscillator _modulationLfo;
    private readonly LowFrequencyOscillator _vibratoLfo;

    // Filter
    private readonly LowPassFilter _filter;

    // Modulation amounts
    private double _modEnvToPitch; // cents
    private double _modEnvToFilterFc; // cents
    private double _modLfoToPitch; // cents
    private double _modLfoToFilterFc; // cents
    private double _modLfoToVolume; // centibels
    private double _vibLfoToPitch; // cents

    // LFO timing parameters (kept on the voice so post-Configure adjustments — GM2
    // CC 76/78 — can re-derive the live LFO state).
    private short _vibLfoFreqCents;       // 0 = 8.176 Hz (SF2 default)
    private short _vibLfoDelayTimecents;  // -12000 = no delay (SF2 default)
    private short _modLfoFreqCents;
    private short _modLfoDelayTimecents;

    // Attenuation and pan
    private double _attenuation; // centibels
    private double _pan; // -500 to 500 (-1 to 1)
    private double _velocityAttenuation;

    // Effect sends — SF2 spec stores these in 0.1% units (0..1000 → 0..1.0).
    private double _reverbSend;
    private double _chorusSend;

    // Filter parameters
    private short _filterCutoff;
    private short _filterResonance;

    // State
    private VoiceState _state;
    private int _channel;
    private int _exclusiveClass;
    private int _generationId;

    // Sostenuto pedal (CC66) state. SostenutoHeld means this voice was sounding when
    // the pedal went down and should ignore NoteOff until the pedal lifts.
    // SostenutoReleasePending means NoteOff arrived while held — release when pedal lifts.
    private bool _sostenutoHeld;
    private bool _sostenutoReleasePending;

    // Polyphonic aftertouch (SF2 default modulator #5): per-note vibrato LFO pitch depth
    // contribution in cents, max 50 at pressure=127. Updated by Synthesizer.PolyPressure.
    private double _polyPressureVibDepthCents;

    // Portamento glide: signed pitch offset (cents) that decays toward 0 each sample.
    // _portamentoStepPerSample is unsigned — actual delta is sign(_portamentoCents) * step.
    private double _portamentoCents;
    private double _portamentoStepPerSample;

    /// <summary>
    /// Polyphonic aftertouch contribution to vibrato LFO pitch depth, in cents.
    /// Set by Synthesizer when 0xA0 events arrive matching this voice.
    /// </summary>
    public double PolyPressureVibDepthCents
    {
        get => _polyPressureVibDepthCents;
        set => _polyPressureVibDepthCents = value;
    }

    /// <summary>
    /// Whether the voice is captured by the sostenuto pedal.
    /// </summary>
    public bool SostenutoHeld { get => _sostenutoHeld; set => _sostenutoHeld = value; }

    /// <summary>
    /// True when a NoteOff arrived while sostenuto-held; release on pedal lift.
    /// </summary>
    public bool SostenutoReleasePending { get => _sostenutoReleasePending; set => _sostenutoReleasePending = value; }

    /// <summary>
    /// Current state of the voice.
    /// </summary>
    public VoiceState State => _state;

    /// <summary>
    /// MIDI channel this voice is playing on.
    /// </summary>
    public int Channel => _channel;

    /// <summary>
    /// MIDI key number being played.
    /// </summary>
    public int KeyNumber => _keyNumber;

    /// <summary>
    /// Exclusive class (for muting other voices).
    /// </summary>
    public int ExclusiveClass => _exclusiveClass;

    /// <summary>
    /// Generation ID for voice stealing priority.
    /// </summary>
    public int GenerationId => _generationId;

    /// <summary>
    /// Whether the voice has finished and can be recycled.
    /// </summary>
    public bool IsFinished => _state == VoiceState.Free || _volumeEnvelope.IsFinished;

    /// <summary>
    /// Creates a new voice.
    /// </summary>
    public Voice(int sampleRate)
    {
        _sampleRate = sampleRate;
        _volumeEnvelope = new Envelope(sampleRate);
        _modulationEnvelope = new Envelope(sampleRate);
        _modulationLfo = new LowFrequencyOscillator(sampleRate);
        _vibratoLfo = new LowFrequencyOscillator(sampleRate);
        _filter = new LowPassFilter(sampleRate);
        _state = VoiceState.Free;
        _scaleTuning = 100; // Default: 100 cents per semitone
    }

    /// <summary>
    /// Resets the voice to free state.
    /// </summary>
    public void Reset()
    {
        _state = VoiceState.Free;
        _sampleData = null;
        _position = 0;
        _sostenutoHeld = false;
        _sostenutoReleasePending = false;
        _volumeEnvelope.Reset();
        _modulationEnvelope.Reset();
        _modulationLfo.Reset();
        _vibratoLfo.Reset();
        _filter.Reset();
    }

    /// <summary>
    /// Configures the voice with sample and generator data.
    /// </summary>
    public void Configure(
        float[] sampleData,
        SampleHeader header,
        Zone instrumentZone,
        Zone? presetZone,
        Zone? instrumentGlobalZone,
        Zone? presetGlobalZone,
        int keyNumber,
        int velocity,
        int channel,
        int generationId)
    {
        // Reset all state first to ensure no leftover data from previous voice
        _volumeEnvelope.Reset();
        _modulationEnvelope.Reset();
        _modulationLfo.Reset();
        _vibratoLfo.Reset();
        _filter.Reset();
        _position = 0;

        _sampleData = sampleData;
        _keyNumber = keyNumber;
        _velocity = velocity;
        _channel = channel;
        _generationId = generationId;
        _state = VoiceState.Playing;
        _sostenutoHeld = false;
        _sostenutoReleasePending = false;
        _polyPressureVibDepthCents = 0;
        _portamentoCents = 0;
        _portamentoStepPerSample = 0;

        // Sample parameters
        _sampleStart = header.Start;
        _sampleEnd = header.End;
        _loopStart = header.StartLoop;
        _loopEnd = header.EndLoop;
        // Ensure valid sample rate (default to synth rate if invalid)
        _baseSampleRate = header.SampleRate > 0 ? header.SampleRate : (uint)_sampleRate;
        // OriginalPitch of 255 means "unpitched" - use middle C (60) as default
        _rootKey = header.OriginalPitch == 255 ? 60 : header.OriginalPitch;
        _pitchCorrection = header.PitchCorrection;

        // Default values
        _coarseTune = 0;
        _fineTune = 0;
        _scaleTuning = 100;
        _attenuation = 0;
        _pan = 0;
        _loopMode = LoopMode.NoLoop;
        _exclusiveClass = 0;

        // Modulation defaults
        _modEnvToPitch = 0;
        _modEnvToFilterFc = 0;
        _modLfoToPitch = 0;
        _modLfoToFilterFc = 0;
        _modLfoToVolume = 0;
        _vibLfoToPitch = 0;

        // Filter defaults
        _filterCutoff = 13500; // ~20kHz
        _filterResonance = 0;

        // Effect sends default to 0 (dry) until a generator overrides
        _reverbSend = 0;
        _chorusSend = 0;

        // Set default envelope parameters
        _volumeEnvelope.SetDefaultVolume();
        _modulationEnvelope.SetDefaultModulation();

        // LFO defaults per SF2 spec §8.5: DelayVibLFO/DelayModLFO = -12000 timecents
        // (effectively no delay), FreqVibLFO/FreqModLFO = 0 absolute cents (= 8.176 Hz).
        _vibLfoFreqCents = 0;
        _vibLfoDelayTimecents = -12000;
        _modLfoFreqCents = 0;
        _modLfoDelayTimecents = -12000;

        // Apply generators from global and zone layers
        // Instrument generators set absolute values
        ApplyGenerators(instrumentGlobalZone, isPreset: false);
        ApplyGenerators(instrumentZone, isPreset: false);
        // Preset generators are ADDITIVE offsets (per SF2 spec)
        ApplyGenerators(presetGlobalZone, isPreset: true);
        ApplyGenerators(presetZone, isPreset: true);

        // Now configure both LFOs from the collected timing values. Done after generator
        // pass so absolute (instrument) and additive (preset) layering both land correctly.
        _modulationLfo.SetParameters(_modLfoDelayTimecents, _modLfoFreqCents);
        _vibratoLfo.SetParameters(_vibLfoDelayTimecents, _vibLfoFreqCents);

        // Velocity → initial attenuation per the SF2 default modulator (spec §8.4.1):
        //   src = velocity, transform = concave-unipolar-negative; dst = InitialAttenuation; amount = 960 cB
        // Evaluating the concave source curve from spec §8.1.4 (fig E) for v in [1,127]:
        //   attenuation_cB = 960 * (-40/96) * log10(v/127) = 400 * log10(127/v)
        // This gives ~4 dB attenuation at vel=100, ~20 dB at vel=40, ~84 dB at vel=1 —
        // the dynamic range velocity-sensitive instruments like piano need.
        var vel = velocity < 1 ? 1 : velocity;
        _velocityAttenuation = 400.0 * Math.Log10(127.0 / vel);

        // SF2 default modulator #2 (spec §8.4.2): velocity → InitialFilterFc, amount = -2400 cents,
        // concave-unipolar-negative source. Using the same concave-curve derivation as above:
        //   filter_mod_cents = -2400 * (-0.4) * log10(vel/127) = -960 * log10(127/vel)
        // Result: at vel=127 the filter is unmodulated; at low velocities the cutoff drops
        // up to ~2400 cents (two octaves), darkening soft notes the way real velocity-
        // sensitive instruments do. Applied BEFORE filter.SetParameters below.
        var velFilterMod = -960.0 * Math.Log10(127.0 / vel);
        _filterCutoff = (short)Math.Clamp(_filterCutoff + velFilterMod, -12000, 13500);

        // Initialize pitch increment
        UpdatePitchIncrement(0);

        // Initialize filter
        _filter.SetParameters(_filterCutoff, _filterResonance);

        // Trigger envelopes and LFOs
        _volumeEnvelope.Trigger(keyNumber);
        _modulationEnvelope.Trigger(keyNumber);
        _modulationLfo.Trigger();
        _vibratoLfo.Trigger();

        // Set initial position
        _position = _sampleStart;
    }

    private void ApplyGenerators(Zone? zone, bool isPreset)
    {
        if (zone == null) return;

        foreach (var gen in zone.Generators)
        {
            ApplyGenerator(gen, isPreset);
        }
    }

    private void ApplyGenerator(Generator gen, bool isPreset)
    {
        var val = gen.Amount.Signed;

        switch (gen.Operator)
        {
            // Sample offsets
            case SFGenerator.StartAddrsOffset:
                _sampleStart += (uint)val;
                break;
            case SFGenerator.EndAddrsOffset:
                _sampleEnd += (uint)val;
                break;
            case SFGenerator.StartloopAddrsOffset:
                _loopStart += (uint)val;
                break;
            case SFGenerator.EndloopAddrsOffset:
                _loopEnd += (uint)val;
                break;
            case SFGenerator.StartAddrsCoarseOffset:
                _sampleStart += (uint)(val * 32768);
                break;
            case SFGenerator.EndAddrsCoarseOffset:
                _sampleEnd += (uint)(val * 32768);
                break;
            case SFGenerator.StartloopAddrsCoarseOffset:
                _loopStart += (uint)(val * 32768);
                break;
            case SFGenerator.EndloopAddrsCoarseOffset:
                _loopEnd += (uint)(val * 32768);
                break;

            // Pitch
            case SFGenerator.CoarseTune:
                // Instrument zones set absolute, preset zones add offsets
                if (isPreset)
                    _coarseTune += val;
                else
                    _coarseTune = val;
                break;
            case SFGenerator.FineTune:
                // Instrument zones set absolute, preset zones add offsets
                if (isPreset)
                    _fineTune += val;
                else
                    _fineTune = val;
                break;
            case SFGenerator.ScaleTuning:
                // Preset zones add to scale tuning, instrument zones set it
                if (isPreset)
                    _scaleTuning += val;
                else
                    _scaleTuning = val;
                break;
            case SFGenerator.OverridingRootKey:
                // Root key only comes from instrument zones, not presets
                if (!isPreset && val is >= 0 and <= 127)
                    _rootKey = val;
                break;

            // Volume
            case SFGenerator.InitialAttenuation:
            {
                // EMU8k/10k hardware (and every real-world SF2 renderer including
                // fluidsynth) applies a 0.4 scale factor to InitialAttenuation generator
                // values at both preset and instrument level. Without it, soundfonts
                // authored against EMU hardware (i.e. essentially all of them) render
                // roughly 10 dB quieter than intended.
                const double emuAttenuationFactor = 0.4;
                var scaled = val * emuAttenuationFactor;
                if (isPreset)
                    _attenuation += scaled;
                else
                    _attenuation = scaled;
                break;
            }
            case SFGenerator.Pan:
                // Instrument zones set absolute, preset zones add offsets
                if (isPreset)
                    _pan += val;
                else
                    _pan = val;
                break;

            // Effect sends. SF2 spec §8.1.3 generators 15 & 16: amount is in 0.1% units
            // (1000 = 100%). Preset zones add offsets; instrument zones set absolute.
            case SFGenerator.ReverbEffectsSend:
            {
                var frac = val / 1000.0;
                if (isPreset) _reverbSend += frac;
                else _reverbSend = frac;
                break;
            }
            case SFGenerator.ChorusEffectsSend:
            {
                var frac = val / 1000.0;
                if (isPreset) _chorusSend += frac;
                else _chorusSend = frac;
                break;
            }

            // Loop mode
            case SFGenerator.SampleModes:
                _loopMode = (val & 1) != 0
                    ? (val & 2) != 0 ? LoopMode.LoopUntilRelease : LoopMode.LoopContinuously
                    : LoopMode.NoLoop;
                break;

            // Exclusive class
            case SFGenerator.ExclusiveClass:
                _exclusiveClass = val;
                break;

            // Filter
            case SFGenerator.InitialFilterFc:
                // Instrument zones set absolute, preset zones add offsets
                if (isPreset)
                    _filterCutoff += val;
                else
                    _filterCutoff = val;
                break;
            case SFGenerator.InitialFilterQ:
                // Instrument zones set absolute, preset zones add offsets
                if (isPreset)
                    _filterResonance += val;
                else
                    _filterResonance = val;
                break;

            // Volume envelope — Synthesizer applies these in a dedicated pass via SetVolumeEnvelope
            case SFGenerator.DelayVolEnv:
            case SFGenerator.AttackVolEnv:
            case SFGenerator.HoldVolEnv:
            case SFGenerator.DecayVolEnv:
            case SFGenerator.SustainVolEnv:
            case SFGenerator.ReleaseVolEnv:
            case SFGenerator.KeynumToVolEnvHold:
            case SFGenerator.KeynumToVolEnvDecay:
                break;

            // Modulation envelope — applied via SetModulationEnvelope
            case SFGenerator.DelayModEnv:
            case SFGenerator.AttackModEnv:
            case SFGenerator.HoldModEnv:
            case SFGenerator.DecayModEnv:
            case SFGenerator.SustainModEnv:
            case SFGenerator.ReleaseModEnv:
            case SFGenerator.KeynumToModEnvHold:
            case SFGenerator.KeynumToModEnvDecay:
                break;

            // Modulation LFO timing (instrument zones set absolute, preset zones add).
            case SFGenerator.DelayModLFO:
                if (isPreset) _modLfoDelayTimecents = (short)(_modLfoDelayTimecents + val);
                else _modLfoDelayTimecents = val;
                break;
            case SFGenerator.FreqModLFO:
                if (isPreset) _modLfoFreqCents = (short)(_modLfoFreqCents + val);
                else _modLfoFreqCents = val;
                break;

            // Vibrato LFO timing.
            case SFGenerator.DelayVibLFO:
                if (isPreset) _vibLfoDelayTimecents = (short)(_vibLfoDelayTimecents + val);
                else _vibLfoDelayTimecents = val;
                break;
            case SFGenerator.FreqVibLFO:
                if (isPreset) _vibLfoFreqCents = (short)(_vibLfoFreqCents + val);
                else _vibLfoFreqCents = val;
                break;

            // Modulation routing
            case SFGenerator.ModEnvToPitch:
                if (isPreset)
                    _modEnvToPitch += val;
                else
                    _modEnvToPitch = val;
                break;
            case SFGenerator.ModEnvToFilterFc:
                if (isPreset)
                    _modEnvToFilterFc += val;
                else
                    _modEnvToFilterFc = val;
                break;
            case SFGenerator.ModLfoToPitch:
                if (isPreset)
                    _modLfoToPitch += val;
                else
                    _modLfoToPitch = val;
                break;
            case SFGenerator.ModLfoToFilterFc:
                if (isPreset)
                    _modLfoToFilterFc += val;
                else
                    _modLfoToFilterFc = val;
                break;
            case SFGenerator.ModLfoToVolume:
                if (isPreset)
                    _modLfoToVolume += val;
                else
                    _modLfoToVolume = val;
                break;
            case SFGenerator.VibLfoToPitch:
                if (isPreset)
                    _vibLfoToPitch += val;
                else
                    _vibLfoToPitch = val;
                break;
        }
    }

    /// <summary>
    /// Applies a per-drum-key override (GS NRPN 0x18..0x1E) after Voice.Configure.
    /// Each field of the override is optional — null means "leave whatever the SF2
    /// zone configured alone". Called by Synthesizer at NoteOn on a drum channel.
    /// </summary>
    public void ApplyDrumOverride(DrumKeyOverride ov)
    {
        if (ov.CoarseTune.HasValue) _coarseTune += ov.CoarseTune.Value;
        if (ov.FineTune.HasValue) _fineTune += ov.FineTune.Value;
        if (ov.Level.HasValue && ov.Level.Value < 127)
        {
            // GS drum level: 127 = unity, decreasing values attenuate. Convert to cB.
            // attenuation_cB = 200 * log10(127/v). At v=64 ≈ +60 cB (~6 dB cut).
            var v = Math.Max(1, (int)ov.Level.Value);
            _attenuation += 200.0 * Math.Log10(127.0 / v);
        }
        if (ov.Pan.HasValue && ov.Pan.Value != 0)
        {
            // GS pan 1..127 maps to -500..+500 SF2 units (random=0 left alone here).
            _pan = (ov.Pan.Value - 64) * (500.0 / 63.0);
        }
        if (ov.ReverbSend.HasValue)
            _reverbSend = ov.ReverbSend.Value / 127.0;
        if (ov.ChorusSend.HasValue)
            _chorusSend = ov.ChorusSend.Value / 127.0;

        // Re-derive pitch increment so coarse/fine tune deltas take effect.
        UpdatePitchIncrement(0);
    }

    /// <summary>
    /// Applies GM2 sound-controller adjustments to the vibrato LFO. Called by
    /// Synthesizer right after Configure when CC 76 (rate) or CC 78 (delay) is non-default.
    /// Both deltas are signed offsets in SF2 units (cents for freq, timecents for delay).
    /// </summary>
    public void AdjustVibratoLfo(short freqCentsDelta, short delayTimecentsDelta)
    {
        _vibLfoFreqCents = (short)(_vibLfoFreqCents + freqCentsDelta);
        _vibLfoDelayTimecents = (short)(_vibLfoDelayTimecents + delayTimecentsDelta);
        _vibratoLfo.SetParameters(_vibLfoDelayTimecents, _vibLfoFreqCents);
    }

    /// <summary>
    /// Starts a portamento glide. The voice begins at <paramref name="startCents"/>
    /// offset from its target pitch and decays linearly to 0 over
    /// <paramref name="timeSeconds"/>. Called by Synthesizer right after Configure when
    /// CC 65 is on (or CC 84 was sent) at NoteOn.
    /// </summary>
    public void StartPortamento(double startCents, double timeSeconds)
    {
        _portamentoCents = startCents;
        var totalSamples = timeSeconds * _sampleRate;
        _portamentoStepPerSample = totalSamples > 0 ? Math.Abs(startCents) / totalSamples : Math.Abs(startCents);
    }

    /// <summary>
    /// Adds an extra resonance offset (centibels) on top of whatever the SF2 generators
    /// configured. Called by Synthesizer right after Configure when CC 71 is non-default
    /// at NoteOn — live CC 71 changes after that point do NOT retro-modify this voice.
    /// </summary>
    public void ApplyExtraResonance(double cb)
    {
        _filterResonance = (short)Math.Clamp(_filterResonance + cb, 0, 960);
        _filter.SetParameters(_filterCutoff, _filterResonance);
    }

    /// <summary>
    /// Applies full volume envelope parameters.
    /// </summary>
    public void SetVolumeEnvelope(
        short delay, short attack, short hold, short decay,
        short sustain, short release, short keynumToHold = 0, short keynumToDecay = 0)
    {
        _volumeEnvelope.SetParameters(delay, attack, hold, decay, sustain, release, keynumToHold, keynumToDecay);
    }

    /// <summary>
    /// Applies full modulation envelope parameters.
    /// </summary>
    public void SetModulationEnvelope(
        short delay, short attack, short hold, short decay,
        short sustain, short release, short keynumToHold = 0, short keynumToDecay = 0)
    {
        _modulationEnvelope.SetParameters(delay, attack, hold, decay, sustain, release, keynumToHold, keynumToDecay);
    }

    /// <summary>
    /// Releases the voice (note off).
    /// </summary>
    public void Release()
    {
        if (_state != VoiceState.Playing)
            return;

        _state = VoiceState.Released;
        _volumeEnvelope.Release();
        _modulationEnvelope.Release();

        // If loop until release, continue to end
        if (_loopMode == LoopMode.LoopUntilRelease)
            _loopMode = LoopMode.NoLoop;
    }

    /// <summary>
    /// Forces the voice to stop immediately.
    /// </summary>
    public void Kill()
    {
        _state = VoiceState.Free;
    }

    /// <summary>
    /// Updates pitch with pitch bend modulation.
    /// </summary>
    public void UpdatePitchIncrement(double pitchBendCents)
    {
        // Calculate pitch in cents from root
        var pitchCents = (_keyNumber - _rootKey) * _scaleTuning;
        pitchCents += _coarseTune * 100;
        pitchCents += _fineTune;
        pitchCents += _pitchCorrection;
        pitchCents += pitchBendCents;

        // Sample increment in source frames per output frame:
        // ratio = 2^(cents/1200) * (sourceRate / outputRate)
        var ratio = Math.Pow(2.0, pitchCents / 1200.0) * _baseSampleRate / _sampleRate;
        _increment = ratio;
    }

    /// <summary>
    /// Generates audio samples into the output buffer.
    /// </summary>
    /// <param name="leftBuffer">Left channel output buffer</param>
    /// <param name="rightBuffer">Right channel output buffer</param>
    /// <param name="pitchBendCents">Current pitch bend in cents</param>
    /// <param name="channelVolume">Channel volume (0-1)</param>
    /// <param name="channelExpression">Channel expression (0-1)</param>
    public void Process(
        Span<float> leftBuffer,
        Span<float> rightBuffer,
        Span<float> reverbSendBuffer,
        Span<float> chorusSendBuffer,
        double pitchBendCents,
        double channelVolume,
        double channelExpression,
        float globalReverbFloor = 0f,
        float globalChorusFloor = 0f,
        double channelVibLfoDepthCents = 0.0,
        double channelPan = 0.0,
        double extraAttenuationCb = 0.0,
        double channelFilterCutoffOffsetCents = 0.0,
        float balanceLeft = 1f,
        float balanceRight = 1f)
    {
        if (_state == VoiceState.Free || _sampleData == null)
            return;

        // Calculate pan gains. The SF2-generator pan (_pan, -500..500) and the MIDI
        // channel pan (channelPan, -500..500 already in the same units after CC10
        // scaling) sum and then clamp — matches GM2 behavior where CC10 nudges a
        // voice's authored pan rather than overriding it.
        // Pan: -500 = full left, 0 = center, 500 = full right
        var effectivePan = Math.Clamp(_pan + channelPan, -500.0, 500.0);
        var panNormalized = Math.Clamp(effectivePan / 500.0, -1.0, 1.0);
        // CC 8 Balance is folded in as a post-pan multiplier on each side.
        var leftGain = Math.Sqrt(0.5 * (1.0 - panNormalized)) * balanceLeft;
        var rightGain = Math.Sqrt(0.5 * (1.0 + panNormalized)) * balanceRight;

        // Hoist send scales — clamp negatives (spec: "0 or less" = no send).
        // The global floor matches fluidsynth's synth.reverb.level behaviour: every voice
        // gets at least the floor amount, even if its SF2 patch specifies no send.
        var reverbSend = Math.Max(globalReverbFloor, (float)Math.Max(0.0, _reverbSend));
        var chorusSend = Math.Max(globalChorusFloor, (float)Math.Max(0.0, _chorusSend));

        // SF2 default modulators 3, 4 & 5 add to VibLfoToPitch at process time so live
        // CC1 / channel-pressure / poly-pressure sweeps affect sustained notes, not just
        // newly-triggered ones. The patch-specified _vibLfoToPitch is summed in unchanged.
        var effectiveVibLfoToPitch =
            _vibLfoToPitch + channelVibLfoDepthCents + _polyPressureVibDepthCents;

        // Process each sample
        for (var i = 0; i < leftBuffer.Length; i++)
        {
            // Process envelopes and LFOs
            var volEnv = _volumeEnvelope.Process();
            var modEnv = _modulationEnvelope.Process();
            var modLfo = _modulationLfo.Process();
            var vibLfo = _vibratoLfo.Process();

            // Check if voice has finished
            if (_volumeEnvelope.IsFinished)
            {
                _state = VoiceState.Free;
                return;
            }

            // Calculate pitch modulation
            var pitchMod = pitchBendCents;
            pitchMod += modEnv * _modEnvToPitch;
            pitchMod += modLfo * _modLfoToPitch;
            pitchMod += vibLfo * effectiveVibLfoToPitch;

            // Portamento glide: decay toward 0 each sample. Stop when the sign flips.
            if (_portamentoCents != 0.0)
            {
                pitchMod += _portamentoCents;
                if (_portamentoCents > 0)
                {
                    _portamentoCents -= _portamentoStepPerSample;
                    if (_portamentoCents < 0) _portamentoCents = 0;
                }
                else
                {
                    _portamentoCents += _portamentoStepPerSample;
                    if (_portamentoCents > 0) _portamentoCents = 0;
                }
            }

            // Per-sample pitch increment with modulation
            var pitchCents = (_keyNumber - _rootKey) * _scaleTuning;
            pitchCents += _coarseTune * 100 + _fineTune + _pitchCorrection + pitchMod;
            var increment = Math.Pow(2.0, pitchCents / 1200.0) * _baseSampleRate / _sampleRate;

            // Get interpolated sample (already normalized to [-1, 1])
            var sample = GetInterpolatedSample();

            // Apply filter with modulation
            if (_filter.Enabled)
            {
                // CC 74 (brightness) folds in here as a per-channel live cutoff offset,
                // on top of modEnv and modLfo routings. Cents are additive in log space.
                var filterMod = modEnv * _modEnvToFilterFc + modLfo * _modLfoToFilterFc
                                                           + channelFilterCutoffOffsetCents;
                _filter.ModulateCutoff(filterMod);
                sample = _filter.Process(sample);
            }

            // Calculate volume with envelope and modulation
            var volumeMod = modLfo * _modLfoToVolume; // centibels
            var totalAttenuation = _attenuation + _velocityAttenuation + volumeMod + extraAttenuationCb;
            var gain = Math.Pow(10.0, -totalAttenuation / 200.0);
            gain *= volEnv;
            gain *= channelVolume * channelExpression;

            // Apply gain and pan (sample is already normalized — no /32768 needed)
            var outputSample = (float)(sample * gain);
            leftBuffer[i] += outputSample * (float)leftGain;
            rightBuffer[i] += outputSample * (float)rightGain;

            // Effect sends: post-attenuation/envelope mono signal, scaled by per-voice
            // send amount. The global reverb/chorus instances mix their wet result back
            // into the main L/R buffer after all voices have been processed.
            if (reverbSend > 0f) reverbSendBuffer[i] += outputSample * reverbSend;
            if (chorusSend > 0f) chorusSendBuffer[i] += outputSample * chorusSend;

            // Advance position
            _position += increment;

            // Handle looping
            if (_loopMode != LoopMode.NoLoop && _position >= _loopEnd)
            {
                _position = _loopStart + (_position - _loopEnd);
            }
            else if (_position >= _sampleEnd)
            {
                _state = VoiceState.Free;
                return;
            }
        }
    }

    /// <summary>
    /// Gets an interpolated sample value at the current position using 7-point
    /// windowed-sinc interpolation. This is the standard upgrade over Hermite for
    /// SF2 synthesis: preserves substantially more HF content (much closer to the
    /// ideal anti-aliased sample-rate conversion), at the cost of 7 multiplies per
    /// output sample instead of 3. Coefficients come from <see cref="SincInterpolator"/>.
    /// </summary>
    private double GetInterpolatedSample()
    {
        var index = (int)_position;
        var frac = _position - index;

        if (index < 0 || index >= _sampleData!.Length)
            return 0;

        // Inlined sinc evaluation: fetch the 7-tap coefficient slice for this fractional
        // position, multiply by 7 neighbouring sample values (with loop/boundary handling),
        // sum. Avoids a per-sample delegate allocation that would otherwise dominate cost.
        var fIdx = (int)(frac * SincInterpolator.FractionSlots);
        if (fIdx >= SincInterpolator.FractionSlots) fIdx = SincInterpolator.FractionSlots - 1;
        var coeffs = SincInterpolator.Coefficients;
        var baseOff = fIdx * SincInterpolator.Width;

        // HalfWidth = 3 → taps at index-3, index-2, …, index+3.
        return coeffs[baseOff + 0] * ReadSampleAt(index - 3)
             + coeffs[baseOff + 1] * ReadSampleAt(index - 2)
             + coeffs[baseOff + 2] * ReadSampleAt(index - 1)
             + coeffs[baseOff + 3] * _sampleData[index]
             + coeffs[baseOff + 4] * ReadSampleAt(index + 1)
             + coeffs[baseOff + 5] * ReadSampleAt(index + 2)
             + coeffs[baseOff + 6] * ReadSampleAt(index + 3);
    }

    /// <summary>
    /// Reads one sample at an absolute frame index, honouring loop boundaries.
    /// Returns 0 for indices before the buffer start or beyond the sample end
    /// (when not looping).
    /// </summary>
    private double ReadSampleAt(int idx)
    {
        // Callers already guard on _sampleData being non-null (only invoked from inside
        // the per-sample loop in Process, which is itself guarded). Capture once into a
        // local so the compiler can prove non-null for the rest of the method.
        var data = _sampleData!;

        if (idx < 0)
            // Before the buffer start — clamp to the first valid sample. The interpolator
            // uses this for the left neighbour at the very start of a sample.
            return data[0];

        var looping = _loopMode != LoopMode.NoLoop;
        var boundary = looping ? _loopEnd : _sampleEnd;

        if (idx < boundary && idx < data.Length)
            return data[idx];

        if (looping)
        {
            // Past loop end — wrap. (idx - loopEnd) tells us how far past we are.
            var loopLen = _loopEnd - _loopStart;
            if (loopLen == 0) return data[_loopStart];
            var offset = (uint)(idx - _loopEnd) % loopLen;
            return data[_loopStart + offset];
        }

        // Past the end of a non-looped sample — clamp to last valid frame.
        return data[Math.Min((int)_sampleEnd - 1, data.Length - 1)];
    }
}
