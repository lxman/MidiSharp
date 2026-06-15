using System;
using System.Collections.Generic;
using MidiSharp.SoundBank;
using IRLoopMode = MidiSharp.SoundBank.LoopMode;

namespace MidiSharp.Synth;

/// <summary>
/// Voice state for tracking active/released voices.
/// </summary>
public enum VoiceState
{
    Free,
    Playing,
    Released,
}

/// <summary>
/// One playing voice. Configured from an IR <see cref="PatchZone"/> at NoteOn,
/// reads sample frames through an <see cref="ISampleSource"/>, evaluates the
/// zone's <see cref="ModulationRoute"/> list once per audio block, and renders
/// stereo + send signals into the synth's mix buffers.
/// </summary>
/// <remarks>
/// Format-neutral: the voice has no SF2-specific code. Anything format-specific
/// lives in the loader that produced the <see cref="PatchZone"/>; the synth
/// itself works the same whether the source bank was SF2, SFZ, or DLS.
/// </remarks>
public sealed class Voice
{
    // ── Construction ──────────────────────────────────────────────────

    private readonly int _sampleRate;
    private readonly Envelope _volumeEnvelope;
    private readonly Envelope _modulationEnvelope;
    private readonly LowFrequencyOscillator _vibratoLfo;
    private readonly LowFrequencyOscillator _modulationLfo;
    private readonly LowPassFilter _filter;

    // ── Sample addressing ─────────────────────────────────────────────

    private ISampleSource? _sampleSource;
    private int _sampleId;
    private long _sampleStart;       // first frame to play (sample-relative)
    private long _sampleEnd;         // one past last frame
    private long _loopStart;
    private long _loopEnd;
    private int _baseSampleRate;
    private IRLoopMode _loopMode;

    // Playback position is in source frames, sample-relative, fractional.
    private double _position;

    // Scratch buffer for ISampleSource reads. The interpolator needs index-3
    // through index+3 (7-tap sinc); we batch-read ScratchSize frames around the
    // current position and refill when we approach an edge. One ReadFrames
    // virtual call per ~250 sample advances instead of per sample.
    private const int ScratchSize = 256;
    private const int InterpolatorHalfWidth = 3;
    private readonly float[] _scratch = new float[ScratchSize];
    private long _scratchBaseFrame;
    private int _scratchFramesAvailable;

    // ── Pitch ─────────────────────────────────────────────────────────

    private int _rootKey;
    private int _keyNumber;
    private int _velocity;
    // SFZ amp_velcurve_N gain for this note's velocity (1.0 = no custom curve).
    private double _ampVelCurveFactor = 1.0;
    private double _pitchCorrectionCents;
    private double _coarseTuneSemitones;
    private double _fineTuneCents;
    private double _scaleTuningCentsPerKey;
    private double _modEnvPitchDepthCents;       // mod envelope → pitch

    // ── Level & pan ────────────────────────────────────────────────────

    private double _attenuationDb;               // base, from zone
    private double _panNormalized;               // base, -1..+1, from zone

    // ── LFO base depths (zone-authored, augmented by routes per block) ─

    private double _vibLfoPitchDepthCents;
    private double _modLfoPitchDepthCents;
    private double _modLfoVolumeDepthDb;
    private double _modLfoFilterDepthCents;
    private bool _hasModulationLfo;
    private bool _hasModulationEnvelope;

    // LFO timing kept on voice so GM2 CC76/78 mid-flight adjustments
    // can re-derive the LFO state.
    private double _vibLfoFrequencyHz;
    private double _vibLfoDelaySeconds;

    // ── Filter ─────────────────────────────────────────────────────────

    private bool _hasFilter;
    private double _filterBaseCutoffHz;
    private double _filterBaseResonanceDb;
    private double _modEnvFilterDepthCents;
    // The route-evaluator's ModulationLfoFilterDepth and ModulationEnvelopeToFilter
    // destinations are routed back into the per-sample filter cutoff via the
    // zone's static base depths plus this block's RouteContributions.

    // ── Effect sends ──────────────────────────────────────────────────

    private double _reverbSend;
    private double _chorusSend;

    // ── Modulation routes (from zone) ─────────────────────────────────

    private IReadOnlyList<ModulationRoute> _routes = Array.Empty<ModulationRoute>();

    // ── Voice / MIDI state ────────────────────────────────────────────

    private VoiceState _state;
    private int _channel;
    private int _exclusiveGroup;
    private int _generationId;
    private byte _polyPressure;

    // Sostenuto pedal capture
    private bool _sostenutoHeld;
    private bool _sostenutoReleasePending;

    // Portamento glide: signed pitch offset that decays toward 0.
    private double _portamentoCents;
    private double _portamentoStepPerSample;

    // ── Properties ────────────────────────────────────────────────────

    public VoiceState State => _state;
    public int Channel => _channel;
    public int KeyNumber => _keyNumber;
    public int ExclusiveGroup => _exclusiveGroup;
    public int GenerationId => _generationId;
    public bool IsFinished => _state == VoiceState.Free || _volumeEnvelope.IsFinished;

    public bool SostenutoHeld { get => _sostenutoHeld; set => _sostenutoHeld = value; }
    public bool SostenutoReleasePending { get => _sostenutoReleasePending; set => _sostenutoReleasePending = value; }

    /// <summary>
    /// Raw 0..127 polyphonic pressure for this voice's key. Set by Synthesizer
    /// when 0xA0 events arrive matching this voice's (channel, key); the route
    /// evaluator normalizes to 0..1 when a route uses PolyPressure as a source.
    /// </summary>
    public byte PolyPressure { get => _polyPressure; set => _polyPressure = value; }

    public Voice(int sampleRate)
    {
        _sampleRate = sampleRate;
        _volumeEnvelope = new Envelope(sampleRate);
        _modulationEnvelope = new Envelope(sampleRate);
        _vibratoLfo = new LowFrequencyOscillator(sampleRate);
        _modulationLfo = new LowFrequencyOscillator(sampleRate);
        _filter = new LowPassFilter(sampleRate);
        _state = VoiceState.Free;
        _scaleTuningCentsPerKey = 100.0;
    }

    public void Reset()
    {
        _state = VoiceState.Free;
        _sampleSource = null;
        _position = 0;
        _scratchFramesAvailable = 0;
        _scratchBaseFrame = 0;
        _sostenutoHeld = false;
        _sostenutoReleasePending = false;
        _polyPressure = 0;
        _portamentoCents = 0;
        _portamentoStepPerSample = 0;
        _routes = Array.Empty<ModulationRoute>();
        _ampVelCurveFactor = 1.0;
        _volumeEnvelope.Reset();
        _modulationEnvelope.Reset();
        _vibratoLfo.Reset();
        _modulationLfo.Reset();
        _filter.Reset();
    }

    // ── Configure ─────────────────────────────────────────────────────

    /// <summary>
    /// Configures the voice for a NoteOn from an IR zone. The zone's static
    /// fields populate base values; the routes drive per-block modulation
    /// against channel state during <see cref="Process"/>.
    /// </summary>
    public void Configure(
        PatchZone zone,
        ISampleSource sampleSource,
        int keyNumber,
        int velocity,
        int channel,
        int generationId)
    {
        Reset();

        var sampleRef = zone.Sample;
        var metadata = sampleSource.Metadata(sampleRef.SampleId);

        _sampleSource = sampleSource;
        _sampleId = sampleRef.SampleId;

        // Tell the source this sample is about to play. A memory-mapped source issues an async OS
        // prefetch (madvise WILLNEED / PrefetchVirtualMemory) so its pages are warm by the time
        // Process reads them; RAM-resident sources no-op. Must return quickly — it's on the audio
        // thread at NoteOn — and the advisory call does.
        sampleSource.PrepareSample(sampleRef.SampleId);

        _keyNumber = keyNumber;
        _velocity = velocity;
        _channel = channel;
        _generationId = generationId;
        _state = VoiceState.Playing;
        _exclusiveGroup = zone.ExclusiveGroup ?? 0;

        // SFZ amp_velcurve_N: look up this note's gain once (velocity is fixed per note).
        // Replaces the default velocity→attenuation route (the translator drops it).
        _ampVelCurveFactor = zone.AmpVelCurve is { } velCurve
            ? velCurve[Math.Clamp(velocity, 0, 127)]
            : 1.0;

        // Sample addressing — IR fields are sample-relative frames. The metadata's
        // base length/loop points are overridable by the zone's optional offset
        // generators (SF2 StartAddrsOffset etc.).
        long fullLength = metadata.LengthFrames;
        _sampleStart = sampleRef.StartOffset ?? 0;
        _sampleEnd = sampleRef.EndOffset ?? fullLength;
        _loopStart = sampleRef.LoopStartOffset ?? metadata.LoopStartFrames;
        _loopEnd = sampleRef.LoopEndOffset ?? metadata.LoopEndFrames;
        _baseSampleRate = metadata.SampleRate > 0 ? metadata.SampleRate : _sampleRate;
        _rootKey = sampleRef.OverridingRootKey
                   ?? (metadata.RootKey == 255 ? 60 : metadata.RootKey);
        _pitchCorrectionCents = metadata.PitchCorrectionCents;
        _loopMode = sampleRef.LoopMode;

        // Defensive bounds: clamp addressing to the actual sample length and
        // disable looping if the loop window is empty/inverted. Some banks (and
        // especially small SF3 wavetable samples whose loop points were authored
        // for a different bit depth) ship with end < start or zero-length loops.
        if (_sampleEnd > fullLength) _sampleEnd = fullLength;
        if (_sampleEnd <= _sampleStart) _sampleEnd = _sampleStart + 1;
        if (_loopEnd <= _loopStart || _loopEnd > _sampleEnd || _loopStart < _sampleStart)
            _loopMode = IRLoopMode.None;

        // Pitch settings (zone + sample-level tuning sum at Configure time).
        _coarseTuneSemitones = zone.Pitch.CoarseTuneSemitones + sampleRef.CoarseTuneSemitones;
        _fineTuneCents = zone.Pitch.FineTuneCents + sampleRef.FineTuneCents;
        _scaleTuningCentsPerKey = sampleRef.ScaleTuningCentsPerKey;
        _modEnvPitchDepthCents = zone.Pitch.ModulationEnvelopeDepthCents;

        // Level & pan baseline (routes contribute on top per block).
        _attenuationDb = zone.Level.AttenuationDb;
        _panNormalized = zone.Level.Pan;

        // Effect sends baseline.
        _reverbSend = zone.ReverbSend;
        _chorusSend = zone.ChorusSend;

        // Volume envelope (always present).
        var ve = zone.VolumeEnvelope;
        _volumeEnvelope.SetParameters(
            delaySeconds: ve.DelaySeconds,
            attackSeconds: ve.AttackSeconds,
            holdSeconds: ve.HoldSeconds,
            decaySeconds: ve.DecaySeconds,
            sustainLevel: ve.SustainLevel,
            releaseSeconds: ve.ReleaseSeconds,
            keynumToHoldCentsPerKey: ve.KeynumToHoldCentsPerKey,
            keynumToDecayCentsPerKey: ve.KeynumToDecayCentsPerKey);

        // Modulation envelope (optional).
        if (zone.ModulationEnvelope is { } me)
        {
            _modulationEnvelope.SetParameters(
                delaySeconds: me.DelaySeconds,
                attackSeconds: me.AttackSeconds,
                holdSeconds: me.HoldSeconds,
                decaySeconds: me.DecaySeconds,
                sustainLevel: me.SustainLevel,
                releaseSeconds: me.ReleaseSeconds,
                keynumToHoldCentsPerKey: me.KeynumToHoldCentsPerKey,
                keynumToDecayCentsPerKey: me.KeynumToDecayCentsPerKey);
            _hasModulationEnvelope = true;
        }
        else
        {
            _hasModulationEnvelope = false;
            _modEnvPitchDepthCents = 0;     // no envelope to scale; saves a multiply per sample
        }

        // Vibrato LFO (optional). Defaults if zone omits but routes might still
        // drive depth at runtime — emit a 0 Hz LFO and let routes fill depth.
        if (zone.VibratoLFO is { } vlfo)
        {
            _vibLfoDelaySeconds = vlfo.DelaySeconds;
            _vibLfoFrequencyHz = vlfo.FrequencyHz;
            _vibLfoPitchDepthCents = vlfo.PitchDepthCents;
            _vibratoLfo.SetParameters(_vibLfoDelaySeconds, _vibLfoFrequencyHz);
        }
        else
        {
            // Routes (mod wheel, channel pressure, etc.) target VibratoLfoPitchDepth
            // even when the zone doesn't author a vibrato LFO. Configure at the SF2
            // default 8.176 Hz and 0 base depth so route contributions still produce
            // audible vibrato when triggered.
            _vibLfoDelaySeconds = 0;
            _vibLfoFrequencyHz = 8.176;
            _vibLfoPitchDepthCents = 0;
            _vibratoLfo.SetParameters(_vibLfoDelaySeconds, _vibLfoFrequencyHz);
        }

        // Modulation LFO (optional).
        if (zone.ModulationLFO is { } mlfo)
        {
            _modulationLfo.SetParameters(mlfo.DelaySeconds, mlfo.FrequencyHz);
            _modLfoPitchDepthCents = mlfo.PitchDepthCents;
            _modLfoVolumeDepthDb = mlfo.VolumeDepthDb;
            _modLfoFilterDepthCents = mlfo.FilterDepthCents;
            _hasModulationLfo = true;
        }
        else
        {
            _hasModulationLfo = false;
            _modLfoPitchDepthCents = 0;
            _modLfoVolumeDepthDb = 0;
            _modLfoFilterDepthCents = 0;
        }

        // Filter (optional).
        if (zone.Filter is { } f)
        {
            _filterBaseCutoffHz = f.CutoffHz;
            _filterBaseResonanceDb = f.ResonanceDb;
            _modEnvFilterDepthCents = f.EnvelopeDepthCents;
            _filter.SetParameters(_filterBaseCutoffHz, _filterBaseResonanceDb);
            _hasFilter = _filter.Enabled;
            // f.LfoDepthCents is folded into _modLfoFilterDepthCents above when
            // the zone has a mod LFO. If only the filter carries the depth, lift
            // it here so the per-sample loop can apply it uniformly.
            if (_modLfoFilterDepthCents == 0 && f.LfoDepthCents != 0)
                _modLfoFilterDepthCents = f.LfoDepthCents;
        }
        else
        {
            _hasFilter = false;
            _modEnvFilterDepthCents = 0;
        }

        // Stash routes (could be the zone-authored list or empty).
        _routes = zone.Routes ?? Array.Empty<ModulationRoute>();

        // Trigger envelopes and LFOs.
        _volumeEnvelope.Trigger(keyNumber);
        if (_hasModulationEnvelope) _modulationEnvelope.Trigger(keyNumber);
        _vibratoLfo.Trigger();
        if (_hasModulationLfo) _modulationLfo.Trigger();

        _position = _sampleStart;
    }

    // ── Post-Configure mutators (called immediately after NoteOn) ────

    /// <summary>
    /// Applies a per-drum-key override (GS NRPN 0x18..0x1E). Fields are optional;
    /// null = leave the zone-authored value alone.
    /// </summary>
    public void ApplyDrumOverride(DrumKeyOverride ov)
    {
        if (ov.CoarseTune.HasValue) _coarseTuneSemitones += ov.CoarseTune.Value;
        if (ov.FineTune.HasValue) _fineTuneCents += ov.FineTune.Value;
        if (ov.Level.HasValue && ov.Level.Value < 127)
        {
            // GS drum level: 127 = unity; lower values attenuate logarithmically.
            // 20 * log10(127/v) dB matches the existing behavior.
            var v = Math.Max(1, (int)ov.Level.Value);
            _attenuationDb += 20.0 * Math.Log10(127.0 / v);
        }
        if (ov.Pan.HasValue && ov.Pan.Value != 0)
        {
            // GS pan 1..127 maps to -1..+1 normalized (1=hard left, 64=center, 127=hard right).
            _panNormalized = (ov.Pan.Value - 64) / 63.0;
        }
        if (ov.ReverbSend.HasValue) _reverbSend = ov.ReverbSend.Value / 127.0;
        if (ov.ChorusSend.HasValue) _chorusSend = ov.ChorusSend.Value / 127.0;
    }

    /// <summary>
    /// Applies GM2 sound-controller adjustments to the vibrato LFO. Both deltas
    /// are domain-typed: <paramref name="freqOctavesDelta"/> in octaves
    /// (positive = faster) and <paramref name="delaySecondsDelta"/> as a time
    /// offset (positive = longer delay).
    /// </summary>
    public void AdjustVibratoLfo(double freqOctavesDelta, double delaySecondsDelta)
    {
        _vibLfoFrequencyHz *= Math.Pow(2.0, freqOctavesDelta);
        _vibLfoDelaySeconds = Math.Max(0, _vibLfoDelaySeconds + delaySecondsDelta);
        _vibratoLfo.SetParameters(_vibLfoDelaySeconds, _vibLfoFrequencyHz);
    }

    /// <summary>
    /// Starts a portamento glide. Voice's pitch begins offset by
    /// <paramref name="startCents"/> and decays linearly to 0 over
    /// <paramref name="timeSeconds"/>.
    /// </summary>
    public void StartPortamento(double startCents, double timeSeconds)
    {
        _portamentoCents = startCents;
        var totalSamples = timeSeconds * _sampleRate;
        _portamentoStepPerSample = totalSamples > 0
            ? Math.Abs(startCents) / totalSamples
            : Math.Abs(startCents);
    }

    /// <summary>
    /// Bakes an additional resonance offset onto the filter (CC 71 at NoteOn).
    /// Live CC 71 changes after this point don't retrofit.
    /// </summary>
    public void ApplyExtraResonance(double db)
    {
        _filterBaseResonanceDb += db;
        _filter.SetParameters(_filterBaseCutoffHz, _filterBaseResonanceDb);
        _hasFilter = _filter.Enabled;
    }

    /// <summary>
    /// Scales envelope time stages by 2^(offsetOctaves). Applied by Synthesizer
    /// for GM2 CC 72 (release), CC 73 (attack), CC 75 (decay). Affects both the
    /// volume and modulation envelopes uniformly per RP-021 convention.
    /// </summary>
    public void ApplyEnvelopeTimeScaling(double attackOctaves, double decayOctaves, double releaseOctaves)
    {
        // The envelopes' SetParameters methods take absolute seconds; rather than
        // re-derive from the zone (which Voice doesn't retain), expose a scale
        // method on Envelope so existing state gets multiplied in place.
        _volumeEnvelope.ScaleStageTimes(attackOctaves, decayOctaves, releaseOctaves);
        if (_hasModulationEnvelope)
            _modulationEnvelope.ScaleStageTimes(attackOctaves, decayOctaves, releaseOctaves);
    }

    // ── Release / kill ────────────────────────────────────────────────

    public void Release()
    {
        if (_state != VoiceState.Playing) return;
        _state = VoiceState.Released;
        _volumeEnvelope.Release();
        if (_hasModulationEnvelope) _modulationEnvelope.Release();
        if (_loopMode == IRLoopMode.UntilRelease) _loopMode = IRLoopMode.None;
    }

    public void Kill() => _state = VoiceState.Free;

    // ── Render ────────────────────────────────────────────────────────

    /// <summary>
    /// Per-block render. Evaluates the zone's routes once against the channel
    /// state to compute additive contributions to each modulation destination,
    /// then runs the per-sample loop applying envelopes, LFOs, filter, gain,
    /// and pan to the output buffers.
    /// </summary>
    /// <param name="extraAttenuationDb">Synth-level contribution that isn't a
    /// route (soft pedal CC 67, CC 92 tremolo). Added to the per-block
    /// attenuation budget.</param>
    /// <param name="nonBendPitchCents">Synth-level pitch offset that isn't pitch
    /// bend (channel fine/coarse tune, master tune, master key shift).</param>
    /// <param name="balanceLeft">CC 8 left-channel gain multiplier (0..1).</param>
    /// <param name="balanceRight">CC 8 right-channel gain multiplier (0..1).</param>
    /// <param name="extraVibLfoDepthCents">CC 77 bipolar contribution to
    /// vibrato depth (not a route — bipolar-around-64 doesn't fit the standard
    /// route source model).</param>
    /// <param name="globalReverbFloor">Minimum reverb send applied to every
    /// voice regardless of zone-authored level (mirrors fluidsynth's
    /// <c>synth.reverb.level</c>).</param>
    public void Process(
        Span<float> leftBuffer,
        Span<float> rightBuffer,
        Span<float> reverbSendBuffer,
        Span<float> chorusSendBuffer,
        ChannelState channelState,
        double extraAttenuationDb = 0,
        double nonBendPitchCents = 0,
        float balanceLeft = 1f,
        float balanceRight = 1f,
        double extraVibLfoDepthCents = 0,
        float globalReverbFloor = 0f,
        float globalChorusFloor = 0f)
    {
        if (_state == VoiceState.Free || _sampleSource == null) return;

        RouteEvaluator.Evaluate(_routes, _velocity, _keyNumber, _polyPressure,
                                channelState, out var contrib);

        // Effective per-block values: zone base + route contribution.
        double effectiveAttenuationDb = _attenuationDb + contrib.AttenuationDb + extraAttenuationDb;
        double effectivePan = Math.Clamp(_panNormalized + contrib.PanNormalized, -1.0, 1.0);
        double effectiveVibLfoDepthCents =
            _vibLfoPitchDepthCents + contrib.VibratoLfoPitchDepthCents + extraVibLfoDepthCents;
        double effectiveModLfoPitchDepthCents = _modLfoPitchDepthCents + contrib.ModulationLfoPitchDepthCents;
        double effectiveModLfoVolumeDepthDb = _modLfoVolumeDepthDb + contrib.ModulationLfoVolumeDepthDb;
        double effectiveModLfoFilterDepthCents = _modLfoFilterDepthCents + contrib.ModulationLfoFilterDepthCents;
        double effectiveModEnvFilterDepthCents = _modEnvFilterDepthCents + contrib.ModulationEnvelopeToFilterCents;
        double effectiveModEnvPitchDepthCents = _modEnvPitchDepthCents + contrib.ModulationEnvelopeToPitchCents;

        double effectiveReverbSend = Math.Max(0.0, _reverbSend + contrib.ReverbSendAmount);
        double effectiveChorusSend = Math.Max(0.0, _chorusSend + contrib.ChorusSendAmount);
        float reverbSendScale = Math.Max(globalReverbFloor, (float)effectiveReverbSend);
        float chorusSendScale = Math.Max(globalChorusFloor, (float)effectiveChorusSend);

        // Equal-power pan + CC 8 balance.
        var leftGain = Math.Sqrt(0.5 * (1.0 - effectivePan)) * balanceLeft;
        var rightGain = Math.Sqrt(0.5 * (1.0 + effectivePan)) * balanceRight;

        // Base pitch in cents (not including bend or modulation — those add per sample).
        double baseStaticPitchCents =
            (_keyNumber - _rootKey) * _scaleTuningCentsPerKey
            + _coarseTuneSemitones * 100.0
            + _fineTuneCents
            + _pitchCorrectionCents
            + nonBendPitchCents
            + contrib.PitchCents;

        // Filter resonance can be modulated per block; cutoff changes per sample.
        double filterResonanceDb = _filterBaseResonanceDb + contrib.FilterResonanceDb;
        if (_hasFilter && contrib.FilterResonanceDb != 0)
            _filter.SetParameters(_filterBaseCutoffHz, filterResonanceDb);

        for (int i = 0; i < leftBuffer.Length; i++)
        {
            double volEnv = _volumeEnvelope.Process();
            double modEnv = _hasModulationEnvelope ? _modulationEnvelope.Process() : 0.0;
            double vibLfo = _vibratoLfo.Process();
            double modLfo = _hasModulationLfo ? _modulationLfo.Process() : 0.0;

            if (_volumeEnvelope.IsFinished)
            {
                _state = VoiceState.Free;
                return;
            }

            // Pitch modulation (additive cents).
            double pitchMod = vibLfo * effectiveVibLfoDepthCents
                            + modLfo * effectiveModLfoPitchDepthCents
                            + modEnv * effectiveModEnvPitchDepthCents;

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

            double pitchCents = baseStaticPitchCents + pitchMod;
            double increment = Math.Pow(2.0, pitchCents / 1200.0) * _baseSampleRate / _sampleRate;

            double sample = GetInterpolatedSample();

            if (_hasFilter)
            {
                double filterCents = modEnv * effectiveModEnvFilterDepthCents
                                   + modLfo * effectiveModLfoFilterDepthCents
                                   + contrib.FilterCutoffCents;
                _filter.ModulateCutoff(filterCents);
                sample = _filter.Process(sample);
            }

            // Gain: zone attenuation + route contribution + envelope + LFO volume mod,
            // then the SFZ amp_velcurve_N factor (1.0 when no custom curve; a flat
            // multiply so a curve value of 0 yields true silence without a dB log).
            double volumeMod = modLfo * effectiveModLfoVolumeDepthDb;
            double totalAttenuationDb = effectiveAttenuationDb + volumeMod;
            double gain = Math.Pow(10.0, -totalAttenuationDb / 20.0) * volEnv * _ampVelCurveFactor;

            float outputSample = (float)(sample * gain);
            leftBuffer[i] += outputSample * (float)leftGain;
            rightBuffer[i] += outputSample * (float)rightGain;

            if (reverbSendScale > 0f) reverbSendBuffer[i] += outputSample * reverbSendScale;
            if (chorusSendScale > 0f) chorusSendBuffer[i] += outputSample * chorusSendScale;

            _position += increment;

            if (_loopMode != IRLoopMode.None && _position >= _loopEnd)
            {
                // Modulo-normalize the overshoot so a single-block advance that
                // crosses many loop iterations (small wavetable samples played
                // high; SF3 in particular) still wraps cleanly into [loopStart,
                // loopEnd). Single-subtract wrap can grow position unboundedly
                // when increment-per-block exceeds the loop length.
                double loopLen = _loopEnd - _loopStart;
                if (loopLen > 0)
                {
                    double overshoot = _position - _loopEnd;
                    _position = _loopStart + (overshoot - Math.Floor(overshoot / loopLen) * loopLen);
                }
                else
                {
                    _position = _loopStart;
                }
            }
            else if (_position >= _sampleEnd)
            {
                _state = VoiceState.Free;
                return;
            }
        }
    }

    // ── Sample reading: scratch-buffered interpolation ───────────────

    /// <summary>
    /// Returns the interpolated sample value at the current fractional position
    /// using 7-tap windowed-sinc. Reads through the scratch buffer; refills the
    /// scratch from <see cref="ISampleSource"/> when the interpolation window
    /// straddles its edge.
    /// </summary>
    private double GetInterpolatedSample()
    {
        int idx = (int)_position;
        double frac = _position - idx;

        int fIdx = (int)(frac * SincInterpolator.FractionSlots);
        if (fIdx >= SincInterpolator.FractionSlots) fIdx = SincInterpolator.FractionSlots - 1;
        var coeffs = SincInterpolator.Coefficients;
        int baseOff = fIdx * SincInterpolator.Width;

        return coeffs[baseOff + 0] * ReadSampleAt(idx - 3)
             + coeffs[baseOff + 1] * ReadSampleAt(idx - 2)
             + coeffs[baseOff + 2] * ReadSampleAt(idx - 1)
             + coeffs[baseOff + 3] * ReadSampleAt(idx + 0)
             + coeffs[baseOff + 4] * ReadSampleAt(idx + 1)
             + coeffs[baseOff + 5] * ReadSampleAt(idx + 2)
             + coeffs[baseOff + 6] * ReadSampleAt(idx + 3);
    }

    /// <summary>
    /// Reads one frame at a sample-relative frame index, going through the
    /// scratch buffer for cache hits and refilling when needed. Honors loop
    /// boundaries; returns 0 before the buffer start.
    /// </summary>
    private double ReadSampleAt(int idx)
    {
        if (idx < 0)
        {
            // Before the first valid frame — clamp to frame 0 (interpolator left edge).
            return ReadFrameDirect(_sampleStart);
        }

        bool looping = _loopMode != IRLoopMode.None;
        long boundary = looping ? _loopEnd : _sampleEnd;
        long absoluteFrame;

        if (idx < boundary)
        {
            absoluteFrame = idx;
        }
        else if (looping)
        {
            long loopLen = _loopEnd - _loopStart;
            if (loopLen <= 0) return ReadFrameDirect(_loopStart);
            long offset = (idx - _loopEnd) % loopLen;
            absoluteFrame = _loopStart + offset;
        }
        else
        {
            // Past the end of a non-looped sample — clamp to last valid frame.
            absoluteFrame = Math.Min((long)idx, _sampleEnd - 1);
        }

        return ReadFrameDirect(absoluteFrame);
    }

    /// <summary>
    /// Reads one frame at an exact sample-relative frame index via the scratch
    /// buffer. Refills the scratch around <paramref name="frame"/> if it falls
    /// outside the currently-cached range.
    /// </summary>
    private double ReadFrameDirect(long frame)
    {
        long local = frame - _scratchBaseFrame;
        if (local < 0 || local >= _scratchFramesAvailable)
        {
            RefillScratch(frame);
            local = frame - _scratchBaseFrame;
            if (local < 0 || local >= _scratchFramesAvailable) return 0.0;
        }
        return _scratch[(int)local];
    }

    /// <summary>
    /// Refills the scratch buffer centered on <paramref name="frame"/>. Aims to
    /// put the read point about a quarter of the way into the buffer so most of
    /// the buffer remains ahead for the playback advance.
    /// </summary>
    private void RefillScratch(long frame)
    {
        long fillStart = Math.Max(0, frame - ScratchSize / 4);
        _scratchBaseFrame = fillStart;
        _scratchFramesAvailable = _sampleSource!.ReadFrames(_sampleId, fillStart, _scratch.AsSpan());
    }
}
