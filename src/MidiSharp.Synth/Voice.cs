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
    // SFZ optional second filter (cutoff2/fil2_type), cascaded in series after _filter.
    private LowPassFilter _filter2 = null!;
    private LowPassFilter _filter2Right = null!;
    private bool _hasFilter2;
    private double _filter2BaseCutoffHz, _filter2ResonanceDb;
    private LfoCcDepth[]? _filter2CutoffCc;
    private readonly LowPassFilter _filterRight;   // right channel's filter state (stereo samples only)

    // SFZ peaking-EQ bands (eqN_*). Pre-allocated and reused; _eqBandCount of them are active this note.
    // _eqRight mirrors them for the right channel of a stereo sample.
    private const int MaxEqBands = 4;
    private readonly PeakingEqFilter[] _eq;
    private readonly PeakingEqFilter[] _eqRight;
    private int _eqBandCount;
    // Base EQ-band params retained so an LFO (lfoN_eqNgain/freq) can modulate around them per block.
    private readonly double[] _eqBaseFreq = new double[MaxEqBands];
    private readonly double[] _eqBaseBw = new double[MaxEqBands];
    private readonly double[] _eqBaseGain = new double[MaxEqBands];
    private readonly int[] _eqBandNumber = new int[MaxEqBands];
    private readonly bool[] _eqLfoDriven = new bool[MaxEqBands];
    private bool _hasLfoEq;

    // ── Sample addressing ─────────────────────────────────────────────

    private ISampleSource? _sampleSource;
    private int _sampleId;
    private long _sampleStart;       // first frame to play (sample-relative)
    private long _sampleEnd;         // one past last frame
    private long _loopStart;
    private long _loopEnd;
    private int _baseSampleRate;
    private IRLoopMode _loopMode;

    // Channel count of the sample this voice plays: 1 = mono (every SF2/SF3 and, pre-stereo-SFZ, SFZ);
    // 2 = interleaved stereo (a frame is L,R in the scratch buffer). The mono path is the original
    // code; the stereo path interpolates each channel and applies pan as a balance trim.
    private int _channels = 1;

    // Playback position is in source frames, sample-relative, fractional.
    private double _position;

    // SFZ delay / delay_random: samples of silence to emit before the voice sounds (counts down).
    private int _delayRemaining;

    // Scratch buffer for ISampleSource reads. The interpolator needs index-3
    // through index+3 (7-tap sinc); we batch-read ScratchSize frames around the
    // current position and refill when we approach an edge. One ReadFrames
    // virtual call per ~250 sample advances instead of per sample.
    private const int ScratchSize = 256;
    private const int InterpolatorHalfWidth = 3;
    // Sized for up to 2 channels interleaved (ScratchSize frames). Mono fills the first ScratchSize
    // slots exactly as before; stereo fills 2*ScratchSize as L,R pairs.
    private readonly float[] _scratch = new float[ScratchSize * 2];
    private long _scratchBaseFrame;
    private int _scratchFramesAvailable;

    // Per-sample memoization. The pitch increment, the filter coefficients, and the dB→linear gain
    // are each a pure function of one slowly-changing input (pitch cents / filter cents / attenuation
    // dB). When nothing is modulating that input it's constant for the whole note, so recomputing the
    // Math.Pow / Sin+Cos every sample is wasted work. Cache keyed on EXACT input equality, so the
    // reused result is the identical double the recompute would produce — output stays bit-for-bit
    // the same. NaN forces a recompute on the first sample of each note (and whenever the input moves).
    private double _cachedPitchCents = double.NaN;
    private double _cachedIncrement;
    private double _cachedFilterCents = double.NaN;
    private double _cachedAttenuationDb = double.NaN;
    private double _cachedLinearGain;

    // ── Pitch ─────────────────────────────────────────────────────────

    private int _rootKey;
    private int _keyNumber;
    private int _velocity;
    // SFZ amp_velcurve_N gain for this note's velocity (1.0 = no custom curve).
    private double _ampVelCurveFactor = 1.0;
    // SFZ live CC crossfades (xfin/xfout_locc) — re-read from channel state every block. Null = none.
    private CcCrossfade[]? _ccCrossfades;
    // SFZ stereo width (mid/side scale): 1.0 = full stereo (no-op), 0 = mono, -1 = swapped.
    private double _widthNorm = 1.0;
    private double _widthBase = 1.0;                 // pre-modulation width; _widthNorm = base + live CC
    private LfoCcDepth[]? _widthCc;                  // SFZ width_oncc (live, per-block)
    // SFZ note_selfmask=off keeps overlapping same-key strikes ringing (read by KillVoicesByChannelKey).
    private bool _noteSelfMask = true;
    // SFZ bend_smooth: one-pole glide of the pitch-route contribution toward its per-block target.
    private double _bendSmoothSeconds;
    private double _smoothedPitchRouteCents;
    private bool _pitchSmoothPrimed;
    // SFZ v2 generic LFOs (lfoN_*) — reusable runners; only the first _genericLfoCount are active.
    private GenericLfoRunner[] _genericLfos = System.Array.Empty<GenericLfoRunner>();
    private int _genericLfoCount;
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
    // SFZ voice-off: when true, a retrigger / off_by group fades this voice (per _offMode/_offTime)
    // instead of hard-killing it. False for SF2/SF3/DLS and plain SFZ → the original abrupt kill.
    private bool _smoothOff;
    private ZoneOffMode _offMode;
    private double _offTimeSeconds;
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
    /// <summary>True when this voice should fade (not hard-cut) if turned off by a retrigger/off_by.</summary>
    public bool SmoothOff => _smoothOff;

    /// <summary>SFZ note_selfmask — false lets a same-key retrigger ring alongside this voice.</summary>
    public bool NoteSelfMask => _noteSelfMask;
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
        _filter2 = new LowPassFilter(sampleRate);
        _filter2Right = new LowPassFilter(sampleRate);
        _filterRight = new LowPassFilter(sampleRate);
        _eq = new PeakingEqFilter[MaxEqBands];
        _eqRight = new PeakingEqFilter[MaxEqBands];
        for (int i = 0; i < MaxEqBands; i++)
        {
            _eq[i] = new PeakingEqFilter(sampleRate);
            _eqRight[i] = new PeakingEqFilter(sampleRate);
        }
        _state = VoiceState.Free;
        _scaleTuningCentsPerKey = 100.0;
    }

    public void Reset()
    {
        _state = VoiceState.Free;
        _sampleSource = null;
        _position = 0;
        _channels = 1;
        _delayRemaining = 0;
        _scratchFramesAvailable = 0;
        _scratchBaseFrame = 0;
        _sostenutoHeld = false;
        _sostenutoReleasePending = false;
        _polyPressure = 0;
        _portamentoCents = 0;
        _portamentoStepPerSample = 0;
        _routes = Array.Empty<ModulationRoute>();
        _ampVelCurveFactor = 1.0;
        _cachedPitchCents = double.NaN;
        _cachedFilterCents = double.NaN;
        _cachedAttenuationDb = double.NaN;
        _volumeEnvelope.Reset();
        _modulationEnvelope.Reset();
        _vibratoLfo.Reset();
        _modulationLfo.Reset();
        _filter.Reset();
        _filterRight.Reset();
        _filter2.Reset();
        _filter2Right.Reset();
        _hasFilter2 = false;
        _filter2CutoffCc = null;
        for (int i = 0; i < MaxEqBands; i++) { _eq[i].Reset(); _eqRight[i].Reset(); }
        _eqBandCount = 0;
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
        int generationId,
        ChannelState channelState)
    {
        Reset();

        var sampleRef = zone.Sample;
        var metadata = sampleSource.Metadata(sampleRef.SampleId);

        _sampleSource = sampleSource;
        _sampleId = sampleRef.SampleId;
        _channels = Math.Clamp(metadata.Channels, 1, 2);   // 1 = mono (original path); 2 = interleaved stereo

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
        _smoothOff = zone.SmoothVoiceOff;
        _offMode = zone.OffMode;
        _offTimeSeconds = zone.OffTimeSeconds;

        // SFZ amp_velcurve_N: look up this note's gain once (velocity is fixed per note).
        // Replaces the default velocity→attenuation route (the translator drops it).
        // The key crossfade (xfin/xfout_lokey/hikey) is likewise fixed per note, so fold it in here.
        _ampVelCurveFactor = zone.AmpVelCurve is { } velCurve
            ? velCurve[Math.Clamp(velocity, 0, 127)]
            : 1.0;
        if (zone.AmpKeyCurve is { } keyCurve)
            _ampVelCurveFactor *= keyCurve[Math.Clamp(keyNumber, 0, 127)];

        // SFZ CC crossfades are live (the controller can move during the note), so keep the tables
        // and re-evaluate them per block in Process rather than baking a single factor here.
        _ccCrossfades = zone.CcCrossfades;

        // SFZ width: a per-voice mid/side scale on stereo samples (no-op at 1.0 / for mono).
        _widthBase = zone.WidthNormalized;
        _widthNorm = _widthBase;
        _widthCc = zone.WidthCc;
        _noteSelfMask = zone.NoteSelfMask;
        _bendSmoothSeconds = zone.BendSmoothSeconds;
        _smoothedPitchRouteCents = 0;
        _pitchSmoothPrimed = false;

        // SFZ v2 generic LFOs: configure one runner per zone LFO (growing the reusable pool as needed).
        if (zone.Lfos is { Length: > 0 } zoneLfos)
        {
            if (_genericLfos.Length < zoneLfos.Length)
            {
                var grown = new GenericLfoRunner[zoneLfos.Length];
                System.Array.Copy(_genericLfos, grown, _genericLfos.Length);
                for (int i = _genericLfos.Length; i < grown.Length; i++) grown[i] = new GenericLfoRunner();
                _genericLfos = grown;
            }
            for (int i = 0; i < zoneLfos.Length; i++) _genericLfos[i].Configure(zoneLfos[i], _sampleRate);
            _genericLfoCount = zoneLfos.Length;
        }
        else
        {
            _genericLfoCount = 0;
        }

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

        // Volume envelope (always present). SFZ ampeg_vel2* adds (velocity/127)×amount to each stage;
        // those amounts are 0 for SF2/SF3/DLS and SFZ without them, so the values below are unchanged
        // (and bit-identical) there.
        var ve = zone.VolumeEnvelope;
        double velNorm = velocity / 127.0;

        // SFZ ampeg_dynamic: evaluate the envelope's CC-modulated stages from the LIVE controller now,
        // at note-on. Null for everything else (SF2/DLS, non-dynamic SFZ), so all those stay
        // byte-identical (every delta below is 0). Sustain offsets are percent (hence /100); times are
        // seconds. Added alongside the ampeg_vel2* per-note offsets in the exact same way.
        double envDelayCc = 0, envAttackCc = 0, envHoldCc = 0, envDecayCc = 0, envSustainCc = 0, envReleaseCc = 0;
        if (ve.CcMods is { } envMods)
            foreach (var m in envMods)
            {
                double o = m.Amount * AriaCurve.Eval(m.Curve, channelState.GetCC(m.Cc) / 127.0);
                switch (m.Stage)
                {
                    case EnvStage.Delay:   envDelayCc += o; break;
                    case EnvStage.Attack:  envAttackCc += o; break;
                    case EnvStage.Hold:    envHoldCc += o; break;
                    case EnvStage.Decay:   envDecayCc += o; break;
                    case EnvStage.Sustain: envSustainCc += o; break;
                    case EnvStage.Release: envReleaseCc += o; break;
                }
            }

        _volumeEnvelope.SetParameters(
            delaySeconds: Math.Max(0.0, ve.DelaySeconds + envDelayCc + velNorm * ve.VelToDelaySeconds),
            attackSeconds: Math.Max(0.0, ve.AttackSeconds + envAttackCc + velNorm * ve.VelToAttackSeconds),
            holdSeconds: Math.Max(0.0, ve.HoldSeconds + envHoldCc + velNorm * ve.VelToHoldSeconds),
            decaySeconds: Math.Max(0.0, ve.DecaySeconds + envDecayCc + velNorm * ve.VelToDecaySeconds),
            sustainLevel: Math.Clamp(ve.SustainLevel + envSustainCc / 100.0 + velNorm * ve.VelToSustainLevel, 0.0, 1.0),
            releaseSeconds: Math.Max(0.0, ve.ReleaseSeconds + envReleaseCc + velNorm * ve.VelToReleaseSeconds),
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
            _vibratoLfo.SetParameters(_vibLfoDelaySeconds, _vibLfoFrequencyHz, vlfo.FadeSeconds);
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
            _modulationLfo.SetParameters(mlfo.DelaySeconds, mlfo.FrequencyHz, mlfo.FadeSeconds);
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
            // SFZ fil_keytrack/fil_keycenter: shift the base cutoff by key distance from the center.
            // KeyTrackCentsPerKey is 0 for SF2/SF3/DLS, so this is a no-op (and bit-identical) there.
            _filterBaseCutoffHz = f.KeyTrackCentsPerKey != 0
                ? f.CutoffHz * Math.Pow(2.0, f.KeyTrackCentsPerKey * (keyNumber - f.KeyTrackCenter) / 1200.0)
                : f.CutoffHz;
            _filterBaseResonanceDb = f.ResonanceDb;
            _modEnvFilterDepthCents = f.EnvelopeDepthCents;
            _filter.Type = f.Type;
            _filterRight.Type = f.Type;
            _filter.SetParameters(_filterBaseCutoffHz, _filterBaseResonanceDb);
            if (_channels == 2) _filterRight.SetParameters(_filterBaseCutoffHz, _filterBaseResonanceDb);
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

        // SFZ second filter (cutoff2/fil2_type), cascaded in series after the first.
        if (zone.Filter2 is { } f2)
        {
            _filter2.Type = f2.Type;
            _filter2Right.Type = f2.Type;
            _filter2BaseCutoffHz = f2.CutoffHz;
            _filter2ResonanceDb = f2.ResonanceDb;
            _filter2CutoffCc = zone.Filter2CutoffCc;
            _filter2.SetParameters(_filter2BaseCutoffHz, _filter2ResonanceDb);
            if (_channels == 2) _filter2Right.SetParameters(_filter2BaseCutoffHz, _filter2ResonanceDb);
            _hasFilter2 = _filter2.Enabled;
        }
        else
        {
            _hasFilter2 = false;
        }

        // SFZ peaking EQ (eqN_*). Empty for SF2/SF3/DLS, so _eqBandCount stays 0 and the per-sample
        // loop skips it entirely (bit-identical there).
        _eqBandCount = Math.Min(zone.EqBands.Count, MaxEqBands);
        for (int i = 0; i < _eqBandCount; i++)
        {
            var band = zone.EqBands[i];
            _eqBaseFreq[i] = band.FrequencyHz;
            _eqBaseBw[i] = band.BandwidthOctaves;
            _eqBaseGain[i] = band.GainDb;
            _eqBandNumber[i] = band.BandNumber;
            _eqLfoDriven[i] = false;
            _eq[i].SetParameters(band.FrequencyHz, band.BandwidthOctaves, band.GainDb);
            if (_channels == 2) _eqRight[i].SetParameters(band.FrequencyHz, band.BandwidthOctaves, band.GainDb);
        }

        // Mark which EQ bands a generic LFO drives (needs both EQ bands and LFO runners configured).
        _hasLfoEq = false;
        for (int g = 0; g < _genericLfoCount; g++)
        {
            var lfo = _genericLfos[g];
            for (int e = 0; e < lfo.EqCount; e++)
            {
                int idx = EqIndexForBand(lfo.EqTargetBand(e));
                if (idx >= 0) { _eqLfoDriven[idx] = true; _hasLfoEq = true; }
            }
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

    /// <summary>Maps an SFZ EQ band number (lfoN_eqNgain/freq target) to its active _eq[] index, or -1.</summary>
    private int EqIndexForBand(int bandNumber)
    {
        for (int i = 0; i < _eqBandCount; i++)
            if (_eqBandNumber[i] == bandNumber) return i;
        return -1;
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
    /// Applies SFZ humanization rolled at NoteOn (the synth owns the RNG so renders stay reproducible):
    /// an extra gain (dB, from amp_random — louder), a detune (cents, pitch_random), an onset delay
    /// (seconds, delay + delay_random) and a sample-start offset (frames, offset_random). All are
    /// constant for the note, so they fold into the base values the per-sample loop already reads.
    /// </summary>
    public void ApplyHumanization(double extraGainDb, double detuneCents, double delaySeconds, long offsetFrames)
    {
        _attenuationDb -= extraGainDb;     // +volume ⇒ −attenuation
        _fineTuneCents += detuneCents;
        if (delaySeconds > 0)
            _delayRemaining = (int)(delaySeconds * _sampleRate);
        if (offsetFrames > 0)
        {
            _sampleStart += offsetFrames;
            if (_sampleStart >= _sampleEnd) _sampleStart = Math.Max(0, _sampleEnd - 1);
            _position = _sampleStart;
        }
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
        if (_channels == 2) _filterRight.SetParameters(_filterBaseCutoffHz, _filterBaseResonanceDb);
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

    /// <summary>
    /// Turns the voice off smoothly (SFZ off_mode/off_time): fade to 0 over ~6 ms (Fast), the zone's
    /// off_time (Time), or its normal ampeg release (Normal) — instead of the abrupt <see cref="Kill"/>.
    /// Matches sfizz's note_polyphony behaviour: a voice that is ALREADY releasing (e.g. a high note
    /// ringing out its long ampeg release when the same key is retriggered) is re-faded from its
    /// current level over off_time, so a trill's previous strikes don't pile up and read as mechanical.
    /// </summary>
    public void TurnOff()
    {
        if (_state == VoiceState.Free) return;

        double releaseSeconds = _offMode switch
        {
            ZoneOffMode.Fast => 0.006,
            ZoneOffMode.Time => _offTimeSeconds,
            _ => -1.0,   // Normal: keep the existing release time
        };
        if (releaseSeconds >= 0) _volumeEnvelope.SetReleaseTime(releaseSeconds);

        if (_state == VoiceState.Playing)
        {
            Release();
        }
        else if (releaseSeconds >= 0)
        {
            // Already releasing — re-fade from the current level over the (shorter) off time.
            _volumeEnvelope.Release();
            if (_hasModulationEnvelope) _modulationEnvelope.Release();
        }
    }

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

        // SFZ live CC crossfades (xfin/xfout_locc): a per-block gain from each controller's current
        // value, so sweeping the CC morphs this layer in/out. Constant within the block.
        double ccCrossfadeGain = 1.0;
        if (_ccCrossfades is { } xfades)
            for (int x = 0; x < xfades.Length; x++)
                ccCrossfadeGain *= xfades[x].Gain[channelState.GetCC(xfades[x].Cc) & 0x7F];

        // SFZ v2 generic LFOs: refresh CC-modulated frequency/depths once per block (CC is constant
        // within the block) so lfoN_freq_oncc and lfoN_{target}_oncc (mod-wheel vibrato) track live.
        for (int g = 0; g < _genericLfoCount; g++)
            _genericLfos[g].BeginBlock(channelState);

        // SFZ width_oncc: live CC modulation of stereo width, refreshed per block (amount is width-%).
        if (_widthCc is { } widthCc)
        {
            double w = 0;
            for (int i = 0; i < widthCc.Length; i++)
                w += channelState.GetCC(widthCc[i].Cc) / 127.0 * widthCc[i].Amount / 100.0;
            _widthNorm = _widthBase + w;
        }

        // SFZ second-filter cutoff modulation (cutoff2_cc): re-coefficient the cascaded filter per block
        // from the live CC (constant within the block). Static second filters skip this.
        if (_hasFilter2 && _filter2CutoffCc is { } f2cc)
        {
            double cents = 0;
            for (int i = 0; i < f2cc.Length; i++)
                cents += channelState.GetCC(f2cc[i].Cc) / 127.0 * f2cc[i].Amount;
            double c2 = _filter2BaseCutoffHz * Math.Pow(2.0, cents / 1200.0);
            _filter2.SetParameters(c2, _filter2ResonanceDb);
            if (_channels == 2) _filter2Right.SetParameters(c2, _filter2ResonanceDb);
        }

        // LFO → EQ (lfoN_eqNgain/freq): recompute each LFO-driven band's biquad once per block from its
        // base params plus the LFO deltas. SetParameters only touches coefficients, not filter state.
        if (_hasLfoEq)
        {
            for (int b = 0; b < _eqBandCount; b++)
            {
                if (!_eqLfoDriven[b]) continue;
                double gainDelta = 0.0, freqDelta = 0.0;
                for (int g = 0; g < _genericLfoCount; g++)
                {
                    var lfo = _genericLfos[g];
                    for (int e = 0; e < lfo.EqCount; e++)
                    {
                        if (EqIndexForBand(lfo.EqTargetBand(e)) != b) continue;
                        if (lfo.EqTargetIsFreq(e)) freqDelta += lfo.EqTargetDelta(e);
                        else gainDelta += lfo.EqTargetDelta(e);
                    }
                }
                double f = _eqBaseFreq[b] + freqDelta;
                double gain = _eqBaseGain[b] + gainDelta;
                _eq[b].SetParameters(f, _eqBaseBw[b], gain);
                if (_channels == 2) _eqRight[b].SetParameters(f, _eqBaseBw[b], gain);
            }
        }

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

        // Pan + CC 8 balance. A mono sample is positioned with an equal-power pan (the original law).
        // A stereo sample already carries its own image, so pan acts as a balance TRIM (attenuate the
        // opposite side, unity at centre) — this keeps a centred stereo sample full-width, matching
        // sfizz's default; an equal-power re-pan would collapse it toward the centre by ~3 dB.
        double leftGain, rightGain;
        if (_channels == 1)
        {
            leftGain = Math.Sqrt(0.5 * (1.0 - effectivePan)) * balanceLeft;
            rightGain = Math.Sqrt(0.5 * (1.0 + effectivePan)) * balanceRight;
        }
        else
        {
            leftGain = (effectivePan <= 0 ? 1.0 : 1.0 - effectivePan) * balanceLeft;
            rightGain = (effectivePan >= 0 ? 1.0 : 1.0 + effectivePan) * balanceRight;
        }

        // SFZ bend_smooth: glide the pitch-route contribution (pitch bend + tune CCs) toward its
        // per-block target with a one-pole filter, instead of jumping. 0 → instant (byte-identical).
        double pitchRouteCents = contrib.PitchCents;
        if (_bendSmoothSeconds > 0.0)
        {
            if (!_pitchSmoothPrimed) { _smoothedPitchRouteCents = pitchRouteCents; _pitchSmoothPrimed = true; }
            else
            {
                double blockSec = leftBuffer.Length / (double)_sampleRate;
                double coeff = 1.0 - Math.Exp(-blockSec / _bendSmoothSeconds);
                _smoothedPitchRouteCents += (pitchRouteCents - _smoothedPitchRouteCents) * coeff;
            }
            pitchRouteCents = _smoothedPitchRouteCents;
        }

        // Base pitch in cents (not including bend or modulation — those add per sample).
        double baseStaticPitchCents =
            (_keyNumber - _rootKey) * _scaleTuningCentsPerKey
            + _coarseTuneSemitones * 100.0
            + _fineTuneCents
            + _pitchCorrectionCents
            + nonBendPitchCents
            + pitchRouteCents;

        // Filter resonance can be modulated per block; cutoff changes per sample.
        double filterResonanceDb = _filterBaseResonanceDb + contrib.FilterResonanceDb;
        if (_hasFilter && contrib.FilterResonanceDb != 0)
        {
            _filter.SetParameters(_filterBaseCutoffHz, filterResonanceDb);
            if (_channels == 2) _filterRight.SetParameters(_filterBaseCutoffHz, filterResonanceDb);
        }

        for (int i = 0; i < leftBuffer.Length; i++)
        {
            // SFZ delay/delay_random: stay silent (envelopes and position frozen) until the onset.
            if (_delayRemaining > 0) { _delayRemaining--; continue; }

            double volEnv = _volumeEnvelope.Process();
            double modEnv = _hasModulationEnvelope ? _modulationEnvelope.Process() : 0.0;
            double vibLfo = _vibratoLfo.Process();
            double modLfo = _hasModulationLfo ? _modulationLfo.Process() : 0.0;

            // SFZ v2 generic LFOs: advance each every sample and sum its per-destination contributions.
            // Volume depth is positive=louder, so it lowers attenuation (negative dB delta).
            double gLfoPitchCents = 0.0, gLfoCutoffCents = 0.0, gLfoAttenDb = 0.0;
            for (int g = 0; g < _genericLfoCount; g++)
            {
                var lfo = _genericLfos[g];
                double lv = lfo.Process();
                gLfoPitchCents += lv * lfo.PitchDepthCents;
                gLfoCutoffCents += lv * lfo.CutoffDepthCents;
                gLfoAttenDb -= lv * lfo.VolumeDepthDb;
            }

            if (_volumeEnvelope.IsFinished)
            {
                _state = VoiceState.Free;
                return;
            }

            // Pitch modulation (additive cents).
            double pitchMod = vibLfo * effectiveVibLfoDepthCents
                            + modLfo * effectiveModLfoPitchDepthCents
                            + modEnv * effectiveModEnvPitchDepthCents
                            + gLfoPitchCents;

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
            if (pitchCents != _cachedPitchCents)   // recompute only when the pitch actually moves
            {
                _cachedPitchCents = pitchCents;
                _cachedIncrement = Math.Pow(2.0, pitchCents / 1200.0) * _baseSampleRate / _sampleRate;
            }
            double increment = _cachedIncrement;

            // Filter cutoff modulation (per-sample), shared by both channels of a stereo sample.
            if (_hasFilter)
            {
                double filterCents = modEnv * effectiveModEnvFilterDepthCents
                                   + modLfo * effectiveModLfoFilterDepthCents
                                   + contrib.FilterCutoffCents
                                   + gLfoCutoffCents;
                if (filterCents != _cachedFilterCents)   // recompute coefficients only when cutoff moves
                {
                    _cachedFilterCents = filterCents;
                    _filter.ModulateCutoff(filterCents);
                    if (_channels == 2) _filterRight.ModulateCutoff(filterCents);
                }
            }

            // Gain: zone attenuation + route contribution + envelope + LFO volume mod,
            // then the SFZ amp_velcurve_N factor (1.0 when no custom curve; a flat
            // multiply so a curve value of 0 yields true silence without a dB log).
            double volumeMod = modLfo * effectiveModLfoVolumeDepthDb;
            double totalAttenuationDb = effectiveAttenuationDb + volumeMod + gLfoAttenDb;
            if (totalAttenuationDb != _cachedAttenuationDb)   // dB→linear only when attenuation moves
            {
                _cachedAttenuationDb = totalAttenuationDb;
                _cachedLinearGain = Math.Pow(10.0, -totalAttenuationDb / 20.0);
            }
            double gain = _cachedLinearGain * volEnv * _ampVelCurveFactor * ccCrossfadeGain;

            if (_channels == 1)
            {
                // Mono path — arithmetically identical to the original when no filter/EQ.
                double sample = GetInterpolatedSample(0);
                if (_hasFilter) sample = _filter.Process(sample);
                if (_hasFilter2) sample = _filter2.Process(sample);
                for (int e = 0; e < _eqBandCount; e++) sample = _eq[e].Process(sample);
                float outputSample = (float)(sample * gain);
                leftBuffer[i] += outputSample * (float)leftGain;
                rightBuffer[i] += outputSample * (float)rightGain;
                if (reverbSendScale > 0f) reverbSendBuffer[i] += outputSample * reverbSendScale;
                if (chorusSendScale > 0f) chorusSendBuffer[i] += outputSample * chorusSendScale;
            }
            else
            {
                // Stereo path — interpolate each channel at the same fractional position; pan/balance
                // is the trim computed above. Sends are mono (the two channels averaged) so a centred
                // stereo sample sends the same energy a mono one would.
                double sampleL = GetInterpolatedSample(0);
                double sampleR = GetInterpolatedSample(1);
                if (_hasFilter)
                {
                    sampleL = _filter.Process(sampleL);
                    sampleR = _filterRight.Process(sampleR);
                }
                if (_hasFilter2)
                {
                    sampleL = _filter2.Process(sampleL);
                    sampleR = _filter2Right.Process(sampleR);
                }
                for (int e = 0; e < _eqBandCount; e++)
                {
                    sampleL = _eq[e].Process(sampleL);
                    sampleR = _eqRight[e].Process(sampleR);
                }
                // SFZ width: mid/side scale. width=1 (default) leaves L/R untouched; 0 → mono (mid),
                // -1 → swapped. The 0.5 mid keeps a centred/correlated signal at unity (level comp).
                if (_widthNorm != 1.0)
                {
                    double mid = (sampleL + sampleR) * 0.5;
                    double side = (sampleL - sampleR) * 0.5 * _widthNorm;
                    sampleL = mid + side;
                    sampleR = mid - side;
                }
                float outL = (float)(sampleL * gain);
                float outR = (float)(sampleR * gain);
                leftBuffer[i] += outL * (float)leftGain;
                rightBuffer[i] += outR * (float)rightGain;
                float send = (outL + outR) * 0.5f;
                if (reverbSendScale > 0f) reverbSendBuffer[i] += send * reverbSendScale;
                if (chorusSendScale > 0f) chorusSendBuffer[i] += send * chorusSendScale;
            }

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
    private double GetInterpolatedSample(int channel)
    {
        int idx = (int)_position;
        double frac = _position - idx;

        int fIdx = (int)(frac * SincInterpolator.FractionSlots);
        if (fIdx >= SincInterpolator.FractionSlots) fIdx = SincInterpolator.FractionSlots - 1;
        var coeffs = SincInterpolator.Coefficients;
        int baseOff = fIdx * SincInterpolator.Width;

        return coeffs[baseOff + 0] * ReadSampleAt(idx - 3, channel)
             + coeffs[baseOff + 1] * ReadSampleAt(idx - 2, channel)
             + coeffs[baseOff + 2] * ReadSampleAt(idx - 1, channel)
             + coeffs[baseOff + 3] * ReadSampleAt(idx + 0, channel)
             + coeffs[baseOff + 4] * ReadSampleAt(idx + 1, channel)
             + coeffs[baseOff + 5] * ReadSampleAt(idx + 2, channel)
             + coeffs[baseOff + 6] * ReadSampleAt(idx + 3, channel);
    }

    /// <summary>
    /// Reads one frame at a sample-relative frame index, going through the
    /// scratch buffer for cache hits and refilling when needed. Honors loop
    /// boundaries; returns 0 before the buffer start.
    /// </summary>
    private double ReadSampleAt(int idx, int channel)
    {
        if (idx < 0)
        {
            // Before the first valid frame — clamp to frame 0 (interpolator left edge).
            return ReadFrameDirect(_sampleStart, channel);
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
            if (loopLen <= 0) return ReadFrameDirect(_loopStart, channel);
            long offset = (idx - _loopEnd) % loopLen;
            absoluteFrame = _loopStart + offset;
        }
        else
        {
            // Past the end of a non-looped sample — clamp to last valid frame.
            absoluteFrame = Math.Min((long)idx, _sampleEnd - 1);
        }

        return ReadFrameDirect(absoluteFrame, channel);
    }

    /// <summary>
    /// Reads one frame at an exact sample-relative frame index via the scratch
    /// buffer. Refills the scratch around <paramref name="frame"/> if it falls
    /// outside the currently-cached range.
    /// </summary>
    private double ReadFrameDirect(long frame, int channel)
    {
        long local = frame - _scratchBaseFrame;
        if (local < 0 || local >= _scratchFramesAvailable)
        {
            RefillScratch(frame);
            local = frame - _scratchBaseFrame;
            if (local < 0 || local >= _scratchFramesAvailable) return 0.0;
        }
        // Mono: _channels==1, channel==0 → _scratch[local] (identical to before). Stereo: interleaved.
        return _scratch[(int)local * _channels + channel];
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
        // Read up to ScratchSize frames. A frame is _channels floats, so the destination span is
        // ScratchSize*_channels wide; ReadFrames returns the frame count. For mono this is the exact
        // 256-float span the original used.
        _scratchFramesAvailable = _sampleSource!.ReadFrames(
            _sampleId, fillStart, _scratch.AsSpan(0, ScratchSize * _channels));
    }
}
