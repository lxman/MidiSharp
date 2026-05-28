using System;
using System.Collections.Generic;
using SF2Net;

namespace MidiSharp.Synth;

/// <summary>
/// SoundFont-based software synthesizer.
/// Manages voice allocation, MIDI processing, and audio generation.
/// </summary>
public sealed class Synthesizer
{
    private const int MaxVoices = 512;
    private const int ChannelCount = 16;

    private readonly Voice[] _voices;
    private readonly ChannelState[] _channels;
    private readonly int _sampleRate;
    private int _generationCounter;

    private SoundFont? _soundFont;
    private float[]? _sampleData;

    // Temporary buffers for mixing
    private float[] _leftBuffer;
    private float[] _rightBuffer;
    private float[] _reverbSendBuffer;
    private float[] _chorusSendBuffer;

    // Per-channel tremolo phase (radians). LFO is sin at ~5.5 Hz; depth = CC 92 / 127.
    // Advanced once per Generate() call so a buffer of N frames gets a single tremolo
    // value — sub-block tremolo would be inaudible (5.5Hz << buffer rate).
    private readonly double[] _tremoloPhase = new double[16];
    private const double TremoloFrequencyHz = 5.5;
    private const double TremoloMaxAttenCb = 60.0;  // ±6 dB at CC92=127

    // Global effect processors driven by per-voice send amounts.
    private readonly Reverb _reverb;
    private readonly Chorus _chorus;

    /// <summary>
    /// Global reverb processor. Adjust RoomSize / Damp / Wet / Width to taste,
    /// or null out individual voices' ReverbEffectsSend generator at load time.
    /// </summary>
    public Reverb Reverb => _reverb;

    /// <summary>
    /// Global chorus processor.
    /// </summary>
    public Chorus Chorus => _chorus;

    /// <summary>
    /// Optional minimum reverb send applied to every voice, regardless of its
    /// SF2-spec ReverbEffectsSend generator or channel CC 91 value. Default 0
    /// (off) since CC 91 handling now provides per-channel send levels per spec.
    /// </summary>
    public float GlobalReverbSend { get; set; } = 0f;

    /// <summary>Optional minimum chorus send applied to every voice. Default 0.</summary>
    public float GlobalChorusSend { get; set; } = 0f;

    /// <summary>
    /// Gets the sample rate of the synthesizer.
    /// </summary>
    public int SampleRate => _sampleRate;

    /// <summary>
    /// Gets the channel states.
    /// </summary>
    public IReadOnlyList<ChannelState> Channels => _channels;

    /// <summary>
    /// Gets the number of active voices.
    /// </summary>
    public int ActiveVoiceCount
    {
        get
        {
            var count = 0;
            foreach (var voice in _voices)
                if (voice.State != VoiceState.Free)
                    count++;
            return count;
        }
    }

    /// <summary>
    /// Gets the currently loaded SoundFont.
    /// </summary>
    public SoundFont? SoundFont => _soundFont;

    /// <summary>
    /// Creates a new synthesizer.
    /// </summary>
    /// <param name="sampleRate">Output sample rate (e.g., 44100, 48000)</param>
    public Synthesizer(int sampleRate = 44100)
    {
        _sampleRate = sampleRate;

        // Initialize voices
        _voices = new Voice[MaxVoices];
        for (var i = 0; i < MaxVoices; i++)
            _voices[i] = new Voice(sampleRate);

        // Initialize channels
        _channels = new ChannelState[ChannelCount];
        for (var i = 0; i < ChannelCount; i++)
        {
            _channels[i] = new ChannelState();
            // Channel 10 (index 9) is percussion per GM convention.
            if (i == 9)
            {
                _channels[i].IsDrumPart = true;
                _channels[i].Bank = 128;
            }
        }

        // Initialize mixing buffers
        _leftBuffer = new float[256];
        _rightBuffer = new float[256];
        _reverbSendBuffer = new float[256];
        _chorusSendBuffer = new float[256];

        // Default reverb / chorus tuning chosen to roughly match fluidsynth defaults.
        _reverb = new Reverb(sampleRate)
        {
            RoomSize = 0.4f,
            Damp = 0.1f,
            Width = 0.5f,
            Wet = 0.9f,
        };
        _chorus = new Chorus(sampleRate)
        {
            Level = 1.0f,
        };
    }

    /// <summary>
    /// Loads a SoundFont for playback.
    /// </summary>
    public void LoadSoundFont(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        var sf = SoundFont.Load(path);
        LoadSoundFont(sf);
    }

    /// <summary>
    /// Loads a SoundFont for playback.
    /// </summary>
    public void LoadSoundFont(SoundFont soundFont)
    {
        if (soundFont == null) throw new ArgumentNullException(nameof(soundFont));

        // Stop all voices
        AllSoundOff();

        _soundFont = soundFont;

        // Decode the whole smpl chunk to normalized float once at load time.
        // The synth's inner loop then works in [-1, 1] without per-sample division.
        var raw = soundFont.RawSampleData;
        if (raw.Length == 0)
        {
            _sampleData = null;
            return;
        }

        var buf = new float[raw.Length];
        const float inv = 1f / 32768f;
        for (var i = 0; i < raw.Length; i++)
            buf[i] = raw[i] * inv;
        _sampleData = buf;
    }

    /// <summary>
    /// Processes a MIDI note on event.
    /// </summary>
    public void NoteOn(int channel, int key, int velocity)
    {
        if ((uint)channel >= ChannelCount) return;     // MIDI is 4-bit channel; silently drop bad input
        if ((uint)key >= 128) return;                  // 7-bit key
        velocity = Math.Clamp(velocity, 0, 127);

        if (velocity == 0)
        {
            NoteOff(channel, key);
            return;
        }

        // CC 88 High Resolution Velocity Prefix (MIDI 1.0 addendum CA-031): when received
        // immediately before NoteOn, supplies 7 extra bits of velocity. We fold those bits
        // in by promoting velocity to a 14-bit value, then re-mapping back to 1..127 with
        // sub-step precision via simple rounding. The actual velocity-attenuation calc
        // inside Voice.Configure is log-based so even small additions register.
        var prefixCh = _channels[channel];
        if (prefixCh.HighResVelocityPrefix != 0)
        {
            int hi = velocity, lo = prefixCh.HighResVelocityPrefix;
            // Combine to 14-bit. Center the LSB so prefix=64 doesn't shift velocity.
            var combined14 = (hi << 7) | lo;
            // Convert back to 7-bit with sub-step rounding: divide by 128, round to nearest.
            velocity = Math.Clamp((combined14 + 64) >> 7, 1, 127);
            prefixCh.HighResVelocityPrefix = 0;  // one-shot per CA-031
        }

        // Retrigger semantics: if the same (channel, key) is already sounding (playing
        // OR releasing), kill it before starting the new voice. For sustained patches
        // (organ, pad) just calling Release would not help — the released voice keeps
        // producing sound through its (often multi-second) release tail and the new
        // voice stacks on top, causing each retrigger to grow louder. Kill is abrupt
        // but matches the behavior of every hardware/software synth I've checked.
        KillVoicesByChannelKey(channel, key);

        if (_soundFont == null || _sampleData == null)
            return;

        var channelState = _channels[channel];

        // Find the preset
        var preset = _soundFont.FindPreset(channelState.Bank, channelState.Program);
        if (preset == null)
        {
            // Try bank 0 as fallback
            preset = _soundFont.FindPreset(0, channelState.Program);
            if (preset == null)
                return;
        }

        // Determine portamento source (key we should glide from). CC 84 (one-shot) wins
        // over LastNoteKey when set, and falls back only when CC 65 (portamento on) is true.
        int portamentoSource = channelState.PortamentoSourceKey >= 0
            ? channelState.PortamentoSourceKey
            : channelState.PortamentoOn ? channelState.LastNoteKey : (sbyte)-1;
        var portamentoStartCents = 0.0;
        var portamentoTimeSeconds = 0.0;
        if (portamentoSource >= 0 && portamentoSource != key)
        {
            portamentoStartCents = (portamentoSource - key) * 100.0;
            // CC 5 0..127 mapped to ~0..6 seconds via quadratic curve so low values are short.
            var t = channelState.PortamentoTimeCc / 127.0;
            portamentoTimeSeconds = t * t * 6.0;
        }
        // CC 84 is consumed at NoteOn whether or not it produced a glide.
        channelState.PortamentoSourceKey = -1;
        // Track this key as the next note's potential source.
        channelState.LastNoteKey = (sbyte)key;

        // Cache the global zones so we can skip them in the iteration loops.
        // Per SF2 spec their generators are applied as defaults inside Voice.Configure,
        // never as a playable zone in their own right.
        var presetGlobal = preset.GlobalZone;

        foreach (var presetZone in preset.Zones)
        {
            if (ReferenceEquals(presetZone, presetGlobal))
                continue;

            if (!presetZone.MatchesKeyVelocity(key, velocity))
                continue;

            // Resolve the instrument referenced by this preset zone
            var instIdx = presetZone.InstrumentIndex;
            if (instIdx < 0 || instIdx >= _soundFont.Instruments.Count)
                continue;
            var instrument = _soundFont.Instruments[instIdx];
            var instGlobal = instrument.GlobalZone;

            foreach (var instZone in instrument.Zones)
            {
                if (ReferenceEquals(instZone, instGlobal))
                    continue;

                if (!instZone.MatchesKeyVelocity(key, velocity))
                    continue;

                // Must have a sample
                if (instZone.Sample == null)
                    continue;

                // Allocate a voice
                var voice = AllocateVoice(channel, key);
                if (voice == null)
                    continue;

                // Handle exclusive class (mute other voices in same class)
                var exclusiveClass = GetExclusiveClass(instZone);
                if (exclusiveClass > 0)
                {
                    KillVoicesByExclusiveClass(channel, exclusiveClass);
                }

                var sampleHeader = instZone.Sample;

                voice.Configure(
                    _sampleData,
                    sampleHeader,
                    instZone,
                    presetZone,
                    instGlobal,
                    presetGlobal,
                    key,
                    velocity,
                    channel,
                    ++_generationCounter);

                ApplyEnvelopeParameters(voice, instZone, instGlobal, presetZone, presetGlobal,
                    channelState);

                // CC 71 Q offset is baked in at NoteOn — live CC 71 changes don't retrofit.
                if (channelState.FilterQOffsetCb != 0)
                    voice.ApplyExtraResonance(channelState.FilterQOffsetCb);

                // Portamento start: voice's pitch begins offset by portamentoStartCents,
                // decays linearly to 0 over portamentoTimeSeconds.
                if (portamentoStartCents != 0.0 && portamentoTimeSeconds > 0.0)
                    voice.StartPortamento(portamentoStartCents, portamentoTimeSeconds);

                // GM2 CC 76 (vibrato rate) and CC 78 (delay): ±1200 cents/timecents at extremes.
                var vibFreqDelta = (short)((channelState.VibratoRateCc - 64) * 1200 / 64);
                var vibDelayDelta = (short)((channelState.VibratoDelayCc - 64) * 1200 / 64);
                if (vibFreqDelta != 0 || vibDelayDelta != 0)
                    voice.AdjustVibratoLfo(vibFreqDelta, vibDelayDelta);

                // GS drum NRPN overrides for this specific (channel, key). Only applied
                // on drum channels (the IsDrumPart check in the NRPN setter handler means
                // overrides never get populated on non-drum channels in the first place).
                if (channelState.DrumOverrides != null &&
                    channelState.DrumOverrides.TryGetValue(key, out var drumOv))
                    voice.ApplyDrumOverride(drumOv);
            }
        }
    }

    private static int GetExclusiveClass(Zone zone)
    {
        foreach (var gen in zone.Generators)
        {
            if (gen.Operator == SFGenerator.ExclusiveClass)
                return gen.Amount.Signed;
        }
        return 0;
    }

    private static void ApplyEnvelopeParameters(Voice voice, Zone instZone, Zone? instGlobal,
        Zone? presetZone, Zone? presetGlobal, ChannelState channelState)
    {
        // Collect envelope parameters from all zones
        // SF2 spec: Instrument zones SET absolute values, Preset zones ADD offsets
        short delayVol = -12000, attackVol = -12000, holdVol = -12000, decayVol = -12000;
        short sustainVol = 0, releaseVol = -12000;
        short keynumToHoldVol = 0, keynumToDecayVol = 0;

        short delayMod = -12000, attackMod = -12000, holdMod = -12000, decayMod = -12000;
        short sustainMod = 0, releaseMod = -12000;
        short keynumToHoldMod = 0, keynumToDecayMod = 0;

        // Apply instrument zones (set absolute values)
        void ApplyInstEnvGens(Zone? zone)
        {
            if (zone == null) return;
            foreach (var gen in zone.Generators)
            {
                switch (gen.Operator)
                {
                    case SFGenerator.DelayVolEnv: delayVol = gen.Amount.Signed; break;
                    case SFGenerator.AttackVolEnv: attackVol = gen.Amount.Signed; break;
                    case SFGenerator.HoldVolEnv: holdVol = gen.Amount.Signed; break;
                    case SFGenerator.DecayVolEnv: decayVol = gen.Amount.Signed; break;
                    case SFGenerator.SustainVolEnv: sustainVol = gen.Amount.Signed; break;
                    case SFGenerator.ReleaseVolEnv: releaseVol = gen.Amount.Signed; break;
                    case SFGenerator.KeynumToVolEnvHold: keynumToHoldVol = gen.Amount.Signed; break;
                    case SFGenerator.KeynumToVolEnvDecay: keynumToDecayVol = gen.Amount.Signed; break;

                    case SFGenerator.DelayModEnv: delayMod = gen.Amount.Signed; break;
                    case SFGenerator.AttackModEnv: attackMod = gen.Amount.Signed; break;
                    case SFGenerator.HoldModEnv: holdMod = gen.Amount.Signed; break;
                    case SFGenerator.DecayModEnv: decayMod = gen.Amount.Signed; break;
                    case SFGenerator.SustainModEnv: sustainMod = gen.Amount.Signed; break;
                    case SFGenerator.ReleaseModEnv: releaseMod = gen.Amount.Signed; break;
                    case SFGenerator.KeynumToModEnvHold: keynumToHoldMod = gen.Amount.Signed; break;
                    case SFGenerator.KeynumToModEnvDecay: keynumToDecayMod = gen.Amount.Signed; break;
                }
            }
        }

        // Apply preset zones (add offsets to instrument values)
        void ApplyPresetEnvGens(Zone? zone)
        {
            if (zone == null) return;
            foreach (var gen in zone.Generators)
            {
                switch (gen.Operator)
                {
                    case SFGenerator.DelayVolEnv: delayVol = (short)(delayVol + gen.Amount.Signed); break;
                    case SFGenerator.AttackVolEnv: attackVol = (short)(attackVol + gen.Amount.Signed); break;
                    case SFGenerator.HoldVolEnv: holdVol = (short)(holdVol + gen.Amount.Signed); break;
                    case SFGenerator.DecayVolEnv: decayVol = (short)(decayVol + gen.Amount.Signed); break;
                    case SFGenerator.SustainVolEnv: sustainVol = (short)(sustainVol + gen.Amount.Signed); break;
                    case SFGenerator.ReleaseVolEnv: releaseVol = (short)(releaseVol + gen.Amount.Signed); break;
                    case SFGenerator.KeynumToVolEnvHold: keynumToHoldVol = (short)(keynumToHoldVol + gen.Amount.Signed); break;
                    case SFGenerator.KeynumToVolEnvDecay: keynumToDecayVol = (short)(keynumToDecayVol + gen.Amount.Signed); break;

                    case SFGenerator.DelayModEnv: delayMod = (short)(delayMod + gen.Amount.Signed); break;
                    case SFGenerator.AttackModEnv: attackMod = (short)(attackMod + gen.Amount.Signed); break;
                    case SFGenerator.HoldModEnv: holdMod = (short)(holdMod + gen.Amount.Signed); break;
                    case SFGenerator.DecayModEnv: decayMod = (short)(decayMod + gen.Amount.Signed); break;
                    case SFGenerator.SustainModEnv: sustainMod = (short)(sustainMod + gen.Amount.Signed); break;
                    case SFGenerator.ReleaseModEnv: releaseMod = (short)(releaseMod + gen.Amount.Signed); break;
                    case SFGenerator.KeynumToModEnvHold: keynumToHoldMod = (short)(keynumToHoldMod + gen.Amount.Signed); break;
                    case SFGenerator.KeynumToModEnvDecay: keynumToDecayMod = (short)(keynumToDecayMod + gen.Amount.Signed); break;
                }
            }
        }

        // Instrument zones set values
        ApplyInstEnvGens(instGlobal);
        ApplyInstEnvGens(instZone);
        // Preset zones add offsets
        ApplyPresetEnvGens(presetGlobal);
        ApplyPresetEnvGens(presetZone);

        // GM2 sound controllers CC 72/73/75: bipolar (64 = no change). Each unit at the
        // extremes represents 2^((cc-64)/64) time multiplier — in SF2 timecents that's a
        // linear add of (cc-64)/64 * 1200 cents. Apply to both vol and mod envelopes
        // (RP-021 doesn't distinguish; treating mod the same as vol is the convention).
        var attackOffset = (short)((channelState.AttackTimeCc - 64) * 1200 / 64);
        var decayOffset = (short)((channelState.DecayTimeCc - 64) * 1200 / 64);
        var releaseOffset = (short)((channelState.ReleaseTimeCc - 64) * 1200 / 64);
        if (attackVol > -12000) attackVol = SaturatingAdd(attackVol, attackOffset);
        if (decayVol > -12000) decayVol = SaturatingAdd(decayVol, decayOffset);
        if (releaseVol > -12000) releaseVol = SaturatingAdd(releaseVol, releaseOffset);
        if (attackMod > -12000) attackMod = SaturatingAdd(attackMod, attackOffset);
        if (decayMod > -12000) decayMod = SaturatingAdd(decayMod, decayOffset);
        if (releaseMod > -12000) releaseMod = SaturatingAdd(releaseMod, releaseOffset);

        voice.SetVolumeEnvelope(delayVol, attackVol, holdVol, decayVol, sustainVol, releaseVol, keynumToHoldVol, keynumToDecayVol);
        voice.SetModulationEnvelope(delayMod, attackMod, holdMod, decayMod, sustainMod, releaseMod, keynumToHoldMod, keynumToDecayMod);
    }

    private static short SaturatingAdd(short a, short b)
    {
        var sum = a + b;
        if (sum > short.MaxValue) return short.MaxValue;
        if (sum < short.MinValue) return short.MinValue;
        return (short)sum;
    }

    /// <summary>
    /// Processes a MIDI note off event.
    /// </summary>
    public void NoteOff(int channel, int key)
    {
        if ((uint)channel >= ChannelCount) return;
        if ((uint)key >= 128) return;

        var channelState = _channels[channel];

        // If sustain pedal is held, don't release yet
        if (channelState.Sustain)
            return;

        ReleaseVoices(channel, key);
    }

    private void ReleaseVoices(int channel, int key)
    {
        foreach (var voice in _voices)
        {
            if (voice.State == VoiceState.Playing &&
                voice.Channel == channel &&
                voice.KeyNumber == key)
            {
                // Sostenuto: if the pedal captures this voice, defer release until
                // the pedal lifts. Subsequent NoteOns on the same key while held
                // still produce new voices (handled by KillVoicesByChannelKey retrigger logic).
                if (voice.SostenutoHeld)
                    voice.SostenutoReleasePending = true;
                else
                    voice.Release();
            }
        }
    }

    /// <summary>
    /// Processes a MIDI control change event.
    /// </summary>
    public void ControlChange(int channel, int controller, int value)
    {
        if ((uint)channel >= ChannelCount) return;
        if ((uint)controller >= 128) return;
        value = Math.Clamp(value, 0, 127);

        var channelState = _channels[channel];

        switch (controller)
        {
            case 0: // Bank Select MSB
                channelState.BankMsb = (byte)value;
                ResolveBank(channelState);
                break;
            case 32: // Bank Select LSB
                channelState.BankLsb = (byte)value;
                ResolveBank(channelState);
                break;
            case 1: // Modulation
                channelState.Modulation = (byte)value;
                break;
            case 7: // Volume
                channelState.Volume = (byte)value;
                break;
            case 10: // Pan
                channelState.Pan = (byte)value;
                break;
            case 11: // Expression
                channelState.Expression = (byte)value;
                break;

            case 8: // Balance
                channelState.Balance = (byte)value;
                break;

            case 80: // GP5
                channelState.Gp5 = (byte)value;
                break;
            case 81: // GP6
                channelState.Gp6 = (byte)value;
                break;
            case 82: // GP7
                channelState.Gp7 = (byte)value;
                break;
            case 83: // GP8
                channelState.Gp8 = (byte)value;
                break;

            case 88: // High Resolution Velocity Prefix (MIDI 1.0 addendum CA-031).
                channelState.HighResVelocityPrefix = (byte)value;
                break;

            case 92: // Tremolo Depth
                channelState.TremoloDepth = (byte)value;
                break;
            case 94: // Detune Depth (no processor)
                channelState.DetuneDepth = (byte)value;
                break;
            case 95: // Phaser Depth (no processor)
                channelState.PhaserDepth = (byte)value;
                break;
            case 64: // Sustain
                var newSustain = value >= 64;
                if (channelState.Sustain && !newSustain)
                {
                    // Release all sustained notes
                    ReleaseAllSustainedVoices(channel);
                }
                channelState.Sustain = newSustain;
                break;

            case 66: // Sostenuto
                var newSostenuto = value >= 64;
                switch (channelState.Sostenuto)
                {
                    case false when newSostenuto:
                    {
                        // Pedal going down — capture every voice currently sounding (Playing only;
                        // already-released voices keep releasing). Subsequent NoteOns won't be
                        // captured, that's the whole point of sostenuto vs sustain.
                        foreach (var voice in _voices)
                        {
                            if (voice.State == VoiceState.Playing && voice.Channel == channel)
                                voice.SostenutoHeld = true;
                        }

                        break;
                    }
                    case true when !newSostenuto:
                    {
                        // Pedal lift — release any captured voices that had NoteOffs while held.
                        foreach (var voice in _voices)
                        {
                            if (voice.State != VoiceState.Playing ||
                                voice.Channel != channel ||
                                !voice.SostenutoHeld) continue;
                            voice.SostenutoHeld = false;
                            if (!voice.SostenutoReleasePending) continue;
                            voice.SostenutoReleasePending = false;
                            voice.Release();
                        }

                        break;
                    }
                }
                channelState.Sostenuto = newSostenuto;
                break;

            case 67: // Soft pedal (una corda)
                channelState.SoftPedal = (byte)value;
                break;

            // GM2 RP-021 sound controllers. Only CC 71 (Q) and CC 74 (brightness) are
            // commonly sent and have well-defined real-time effects on the SF2 filter.
            // Others in 72/73/75-78 aren't tracked — they would only affect new notes
            // and are essentially never present in real MIDI files.
            case 71: // Resonance / Timbre
                channelState.FilterResonanceCc = (byte)value;
                break;
            case 72: // Release Time
                channelState.ReleaseTimeCc = (byte)value;
                break;
            case 73: // Attack Time
                channelState.AttackTimeCc = (byte)value;
                break;
            case 74: // Brightness / Filter Cutoff
                channelState.FilterCutoffCc = (byte)value;
                break;
            case 75: // Decay Time
                channelState.DecayTimeCc = (byte)value;
                break;
            case 76: // Vibrato Rate
                channelState.VibratoRateCc = (byte)value;
                break;
            case 77: // Vibrato Depth
                channelState.VibratoDepthCc = (byte)value;
                break;
            case 78: // Vibrato Delay
                channelState.VibratoDelayCc = (byte)value;
                break;

            case 5: // Portamento Time
                channelState.PortamentoTimeCc = (byte)value;
                break;
            case 65: // Portamento on/off
                channelState.PortamentoOn = value >= 64;
                break;
            case 84: // Portamento control — one-shot source key for the next NoteOn.
                channelState.PortamentoSourceKey = (sbyte)Math.Clamp(value, 0, 127);
                break;

            // --- RPN/Data Entry handling (MIDI 1.0 + RP-018) ---
            // The sequence is: select an RPN with CC 101 (MSB) and CC 100 (LSB), then
            // CC 6 (Data MSB) — and optionally CC 38 (Data LSB) — set its value.
            // RPN 0,0 = Pitch Bend Sensitivity: MSB = semitones, LSB = fractional cents.
            // Sending CC 101=127 + CC 100=127 selects the "Null RPN" which prevents stray
            // Data Entry messages from accidentally modifying real parameters.
            case 101: // RPN MSB
                channelState.RpnMsb = (byte)value;
                channelState.IsNrpnActive = false;
                break;
            case 100: // RPN LSB
                channelState.RpnLsb = (byte)value;
                channelState.IsNrpnActive = false;
                break;
            case 99: // NRPN MSB
                channelState.NrpnMsb = (byte)value;
                channelState.IsNrpnActive = true;
                break;
            case 98: // NRPN LSB
                channelState.NrpnLsb = (byte)value;
                channelState.IsNrpnActive = true;
                break;
            case 6: // Data Entry MSB
                ApplyDataEntryMsb(channelState, value);
                break;
            case 38: // Data Entry LSB
                ApplyDataEntryLsb(channelState, value);
                break;
            case 96: // Data Increment — bump active RPN/NRPN parameter by 1
                ApplyDataIncrement(channelState, +1);
                break;
            case 97: // Data Decrement
                ApplyDataIncrement(channelState, -1);
                break;

            // GM2 / SF2 default modulator 9: CC 91 → ReverbEffectsSend, amount 200 (0.1%).
            // We feed the channel value through to Voice.Process per Generate call so live
            // CC sweeps actually affect sustained notes, not just newly-triggered ones.
            case 91:
                channelState.ReverbSendCc = (byte)value;
                break;
            // GM2 / SF2 default modulator 10: CC 93 → ChorusEffectsSend, amount 200.
            case 93:
                channelState.ChorusSendCc = (byte)value;
                break;
            case 120: // All Sound Off
                KillAllVoices(channel);
                break;
            case 121: // Reset All Controllers
                channelState.Reset();
                break;
            case 122: // Local Control on/off — physical-keyboard-only, no-op for software synth.
                break;
            case 123: // All Notes Off
                ReleaseAllVoices(channel);
                break;

            // Channel mode messages 124-127: per MIDI 1.0 spec all four imply All Notes Off.
            // We don't track Omni/Mono/Poly state — basic GM playback never uses them.
            case 124: // Omni Off
            case 125: // Omni On
            case 126: // Mono On
            case 127: // Poly On
                ReleaseAllVoices(channel);
                break;
        }
    }

    private static void ResolveBank(ChannelState ch)
    {
        // Drum part overrides everything to DrumBank (128 by default; 127 for MAP2),
        // regardless of CC 0/32. Set either by GM default (channel 10) or by GS rhythm-part SysEx.
        if (ch.IsDrumPart)
        {
            ch.Bank = ch.DrumBank;
            return;
        }

        // GS-style soundfonts (incl. GeneralUser-GS) keep CC 0 at 0 and use CC 32 for the
        // variation number; XG-style use CC 0. Prefer LSB if non-zero, else MSB. The
        // NoteOn path additionally falls back to bank 0 if exact match misses, so picking
        // the wrong one here is recoverable.
        ch.Bank = ch.BankLsb != 0 ? ch.BankLsb : ch.BankMsb;
    }

    private static void ApplyDataEntryMsb(ChannelState ch, int value)
    {
        if (ch.IsNrpnActive)
        {
            ApplyNrpnDataEntry(ch, value);
            return;
        }

        switch (ch.RpnMsb)
        {
            // RPN 0,0: Pitch Bend Sensitivity (MSB = semitones)
            case 0 when ch.RpnLsb == 0:
                ch.PitchBendRange = (byte)Math.Clamp(value, 0, 96);
                ch.PitchBendRangeCents = 0; // per RP-018
                return;
            // RPN 0,1: Channel Fine-Tuning — 14-bit signed, MSB is bits 7-13.
            // We need both MSB and LSB to compute, so just store the MSB now and recompute.
            case 0 when ch.RpnLsb == 1:
            {
                // 14-bit value with center 0x2000. Convert (value <<7) part now, LSB later.
                var currentFine14 = (int)Math.Round(ch.FineTuneCents / 100.0 * 8192.0) + 0x2000;
                var lsb = currentFine14 & 0x7F;
                var newFine14 = ((value & 0x7F) << 7) | lsb;
                ch.FineTuneCents = (newFine14 - 0x2000) * (100.0 / 8192.0);
                return;
            }
            // RPN 0,2: Channel Coarse Tuning — MSB only, signed around 0x40.
            case 0 when ch.RpnLsb == 2:
                ch.CoarseTuneSemitones = (sbyte)Math.Clamp(value - 0x40, -64, 63);
                return;
            // RPN 0,3: Tuning Program Select — stored, no MTS impl.
            case 0 when ch.RpnLsb == 3:
                ch.TuningProgram = (byte)value; return;
            // RPN 0,4: Tuning Bank Select — stored, no MTS impl.
            case 0 when ch.RpnLsb == 4:
                ch.TuningBank = (byte)value; return;
            // RPN 0,5: Modulation Depth Range — MSB = semitones part of max vibrato depth.
            // Combined with LSB (cents fraction) it overrides our default 50-cent cap.
            case 0 when ch.RpnLsb == 5:
            {
                var cents = ch.ModulationDepthRangeCents - Math.Floor(ch.ModulationDepthRangeCents / 100.0) * 100.0;
                ch.ModulationDepthRangeCents = value * 100.0 + cents;
                return;
            }
        }
    }

    private static void ApplyDataEntryLsb(ChannelState ch, int value)
    {
        if (ch.IsNrpnActive)
            return;

        if (ch is { RpnMsb: 0, RpnLsb: 0 })
        {
            ch.PitchBendRangeCents = (byte)Math.Clamp(value, 0, 99);
            return;
        }

        if (ch is { RpnMsb: 0, RpnLsb: 1 })
        {
            // Fine tune LSB — update bits 0-6 of the 14-bit value.
            var currentFine14 = (int)Math.Round(ch.FineTuneCents / 100.0 * 8192.0) + 0x2000;
            var msb = (currentFine14 >> 7) & 0x7F;
            var newFine14 = (msb << 7) | (value & 0x7F);
            ch.FineTuneCents = (newFine14 - 0x2000) * (100.0 / 8192.0);
            return;
        }
        // RPN 0,2 (coarse tune) ignores LSB.

        // RPN 0,5 LSB: fractional cents of the modulation depth range.
        if (ch is not { RpnMsb: 0, RpnLsb: 5 }) return;
        var semis = Math.Floor(ch.ModulationDepthRangeCents / 100.0);
        // LSB per RP-021 is interpreted as cents/128 fraction of a semitone.
        ch.ModulationDepthRangeCents = semis * 100.0 + value * 100.0 / 128.0;
    }

    /// <summary>
    /// Dispatches a Data Entry MSB to the currently selected NRPN. Handles two families:
    /// Roland GS drum NRPNs (MSB 0x18..0x1E, LSB = drum key) and Yamaha XG channel NRPNs
    /// (MSB 0x01, LSB = parameter). Anything else is consumed and ignored.
    /// </summary>
    private static void ApplyNrpnDataEntry(ChannelState ch, int value)
    {
        byte msb = ch.NrpnMsb, lsb = ch.NrpnLsb;

        // GS drum NRPN family — only meaningful on a drum part (the LSB is a drum key,
        // 0..127). MSB selects which parameter to set on that key.
        if (ch.IsDrumPart && msb >= 0x18 && msb <= 0x1E)
        {
            ch.DrumOverrides ??= new System.Collections.Generic.Dictionary<int, DrumKeyOverride>();
            if (!ch.DrumOverrides.TryGetValue(lsb, out var ov))
            {
                ov = new DrumKeyOverride();
                ch.DrumOverrides[lsb] = ov;
            }
            switch (msb)
            {
                case 0x18: ov.CoarseTune = (sbyte)Math.Clamp(value - 0x40, -64, 63); break;
                case 0x19: ov.FineTune = (sbyte)Math.Clamp(value - 0x40, -64, 63); break;
                case 0x1A: ov.Level = (byte)value; break;
                case 0x1C: ov.Pan = (byte)value; break;
                case 0x1D: ov.ReverbSend = (byte)value; break;
                case 0x1E: ov.ChorusSend = (byte)value; break;
            }
            return;
        }

        // XG channel NRPN family — MSB=01, LSB selects a per-channel parameter. Many
        // overlap with GM2 CCs; we route them to the same channel-state fields so the
        // downstream code doesn't care which path set them.
        if (msb == 0x01)
        {
            switch (lsb)
            {
                case 0x08: ch.VibratoDepthCc = (byte)value; break;
                case 0x09: ch.VibratoDelayCc = (byte)value; break;
                case 0x0A: ch.VibratoRateCc = (byte)value; break;
                case 0x14: ch.FilterCutoffCc = (byte)value; break;
                case 0x15: ch.FilterResonanceCc = (byte)value; break;
                case 0x18: ch.AttackTimeCc = (byte)value; break;
                case 0x19: ch.ReleaseTimeCc = (byte)value; break;
                // Roland GS sometimes also sends 0x66 EG decay; map to CC 75 equivalent.
                case 0x66: ch.DecayTimeCc = (byte)value; break;
            }
            // Other XG NRPN LSBs accepted but ignored (drum bank select, etc.).
        }
        // Anything else: silently consumed.
    }

    private static void ApplyDataIncrement(ChannelState ch, int delta)
    {
        if (ch.IsNrpnActive) return; // no NRPN handlers; no-op
        switch (ch)
        {
            case { RpnMsb: 0, RpnLsb: 0 }:
                ch.PitchBendRange = (byte)Math.Clamp(ch.PitchBendRange + delta, 0, 96);
                break;
            case { RpnMsb: 0, RpnLsb: 1 }:
                ch.FineTuneCents = Math.Clamp(ch.FineTuneCents + delta, -100, 100);
                break;
            case { RpnMsb: 0, RpnLsb: 2 }:
                ch.CoarseTuneSemitones = (sbyte)Math.Clamp(ch.CoarseTuneSemitones + delta, -64, 63);
                break;
            case { RpnMsb: 0, RpnLsb: 5 }:
                ch.ModulationDepthRangeCents = Math.Max(0, ch.ModulationDepthRangeCents + delta * 100.0);
                break;
        }
    }

    /// <summary>
    /// Processes a MIDI program change event.
    /// </summary>
    public void ProgramChange(int channel, int program)
    {
        if ((uint)channel >= ChannelCount) return;
        if ((uint)program >= 128) return;
        _channels[channel].Program = (byte)program;
    }

    /// <summary>
    /// Processes a MIDI pitch bend event.
    /// </summary>
    public void PitchBend(int channel, int value)
    {
        if ((uint)channel >= ChannelCount) return;
        _channels[channel].PitchBend = (ushort)Math.Clamp(value, 0, 16383);
    }

    /// <summary>
    /// Processes a MIDI polyphonic aftertouch event. Per SF2 default modulator #5
    /// (poly pressure → vibrato LFO pitch depth, amount 50 cents, linear-unipolar),
    /// each matching voice gets up to 50 cents of vibrato depth at full pressure.
    /// </summary>
    public void PolyPressure(int channel, int key, int pressure)
    {
        if ((uint)channel >= ChannelCount) return;
        if ((uint)key >= 128) return;
        pressure = Math.Clamp(pressure, 0, 127);

        var contrib = pressure / 127.0 * 50.0;
        foreach (var voice in _voices)
        {
            if (voice.State != VoiceState.Free &&
                voice.Channel == channel &&
                voice.KeyNumber == key)
            {
                voice.PolyPressureVibDepthCents = contrib;
            }
        }
    }

    /// <summary>
    /// Processes a MIDI channel pressure (monophonic aftertouch) event.
    /// Per SF2 default modulator #3 this adds up to 50 cents of vibrato LFO
    /// pitch depth at full pressure; the actual modulation is applied per-block
    /// inside Generate so live aftertouch sweeps affect sustained notes.
    /// </summary>
    public void ChannelPressure(int channel, int pressure)
    {
        if ((uint)channel >= ChannelCount) return;
        _channels[channel].ChannelPressure = (byte)Math.Clamp(pressure, 0, 127);
    }

    /// <summary>
    /// Processes a MIDI System Exclusive message. Recognises the universal GM Reset,
    /// vendor-specific GS / XG reset sequences (each maps to <see cref="Reset"/>),
    /// and the universal Master Volume message. Other SysEx is ignored.
    /// </summary>
    /// <param name="data">SysEx payload — either including the leading 0xF0 (live MIDI
    /// stream convention) or excluding it (SMF event convention). Trailing 0xF7 optional.</param>
    public void SysEx(ReadOnlySpan<byte> data)
    {
        // Normalise: skip optional leading 0xF0 so the rest of the parser is index-stable.
        if (data.Length > 0 && data[0] == 0xF0)
            data = data[1..];

        if (data.Length < 3) return;

        // Manufacturer ID at data[0]. 0x7E/0x7F are universal non-realtime / realtime IDs.
        switch (data[0])
        {
            case 0x7E: HandleUniversalNonRealtime(data); break;  // GM Reset, MTS bulk dump
            case 0x7F: HandleUniversalRealtime(data); break;     // Master Volume, MTS real-time
            case 0x41: HandleRolandGs(data); break;
            case 0x43: HandleYamahaXg(data); break;
            // Other manufacturer IDs (00 41 42 = various, 00 21 = ...) ignored.
        }
    }

    // ---- Universal SysEx ----

    private void HandleUniversalNonRealtime(ReadOnlySpan<byte> d)
    {
        // 7E dev sub-id1 sub-id2 ... F7
        if (d.Length < 4) return;
        byte sub1 = d[2], sub2 = d[3];

        switch (sub1)
        {
            // GM System On (09 01), GM System Off (09 02), GM2 System On (09 03).
            // All three reset the synth to clean state — GM Off "ends GM mode" but since
            // we don't track a GM-mode flag, Reset() is the right action. GM2 differs from
            // GM1 only in extended controllers (most of which we already implement); we
            // don't switch operating modes, so the Reset semantics are identical.
            case 0x09 when sub2 is 0x01 or 0x02 or 0x03:
                Reset(); return;
            // MIDI Tuning Standard (non-realtime): 7E dev 08 ... — parsed but ignored.
            // We accept and discard so MTS-tagged files don't look like unknown SysEx.
            // sub2 values: 00 = bulk dump request, 01 = bulk dump reply,
            //              03 = single note tuning change, 04 = key-based tuning dump
            case 0x08:
                return;
        }
    }

    private void HandleUniversalRealtime(ReadOnlySpan<byte> d)
    {
        // 7F dev sub-id1 sub-id2 ... F7
        if (d.Length < 4) return;
        byte sub1 = d[2], sub2 = d[3];

        switch (sub1)
        {
            // Master Volume: 7F dev 04 01 LL MM (14-bit, MSB last).
            case 0x04 when sub2 == 0x01 && d.Length >= 6:
            {
                var raw = ((d[5] & 0x7F) << 7) | (d[4] & 0x7F);
                _masterVolume = raw / 16383f;
                return;
            }
            // Master Pan (sometimes called Master Balance): 7F dev 04 02 LL MM — newer
            // addendum, not widely used in real files.
            case 0x04 when sub2 == 0x02 && d.Length >= 6:
            {
                var raw = ((d[5] & 0x7F) << 7) | (d[4] & 0x7F);
                _masterPan = (raw - 8192) / 8192f;  // -1..+1
                return;
            }
            // Master Fine Tuning: 7F dev 04 03 LL MM — 14-bit signed, center 0x2000,
            // range ±100 cents. Same encoding as RPN 0,1 (channel fine tune) but at
            // synth level rather than per-channel.
            case 0x04 when sub2 == 0x03 && d.Length >= 6:
            {
                var raw = ((d[5] & 0x7F) << 7) | (d[4] & 0x7F);
                _masterTuneCents = (raw - 0x2000) * (100.0 / 8192.0);
                return;
            }
            // Master Coarse Tuning: 7F dev 04 04 LL MM — 14-bit signed, center 0x2000,
            // range ±64 semitones (only MSB byte is normally significant).
            case 0x04 when sub2 == 0x04 && d.Length >= 6:
            {
                var raw = ((d[5] & 0x7F) << 7) | (d[4] & 0x7F);
                _masterKeyShiftSemitones = (raw - 0x2000) / 128;  // approx semitones
                return;
            }
            // MTS realtime variants (08 02 single-note, 08 07 scale tuning, ...) — accept/discard.
            case 0x08:
                return;
        }
    }

    // ---- Roland GS ----

    private void HandleRolandGs(ReadOnlySpan<byte> d)
    {
        // 41 dev 42 12 ah am al data... checksum (F7)
        // dev = device id (usually 10), 42 = model GS, 12 = command DT1 (data set).
        if (d.Length < 8 || d[2] != 0x42 || d[3] != 0x12) return;
        int ah = d[4], am = d[5], al = d[6];

        // The "data" region runs from d[7] up to but not including the checksum.
        // The checksum is at d[Length-1] if there's no trailing F7, or d[Length-2] if there is.
        var dataEnd = d.Length - 1;
        if (d[dataEnd] == 0xF7) dataEnd--;
        var payload = d[7..dataEnd];

        switch (ah)
        {
            // System area (ah=40, am=00): master volume/pan/tune/key shift.
            case 0x40 when am == 0x00:
                HandleGsMaster(al, payload);
                return;
            // Reverb/chorus parameter area (ah=40, am=01): patch parameters.
            case 0x40 when am == 0x01:
                HandleGsReverbChorus(al, payload);
                return;
        }

        // Part parameters (ah=40, am=1n where n is the GS part block).
        if (ah != 0x40 || (am & 0xF0) != 0x10) return;
        var channel = GsPartBlockToChannel(am & 0x0F);
        HandleGsPart(channel, al, payload);

        // GS Reset: ah=40, am=00, al=7F, payload=00 — already covered by HandleGsMaster.
    }

    private void HandleGsMaster(int al, ReadOnlySpan<byte> payload)
    {
        switch (al)
        {
            case 0x00 when payload.Length >= 4:
                // Master Tune: four nibbles m1 m2 m3 m4 -> 16-bit value with center 0x0400,
                // range 0x0018..0x07E8 mapped to -100..+100 cents.
                var rawTune = ((payload[0] & 0x0F) << 12) | ((payload[1] & 0x0F) << 8)
                                                          | ((payload[2] & 0x0F) << 4) | (payload[3] & 0x0F);
                _masterTuneCents = (rawTune - 0x0400) / 10.0;  // approx
                break;
            case 0x04 when payload.Length >= 1:
                _masterVolume = payload[0] / 127f;
                break;
            case 0x05 when payload.Length >= 1:
                // Master Key Shift: 0x28..0x58 = -24..+24 semitones (center 0x40).
                _masterKeyShiftSemitones = payload[0] - 0x40;
                break;
            case 0x06 when payload.Length >= 1:
                // Master Pan: 0x01..0x7F = left..right (0x40 center). 0x00 = "random".
                _masterPan = payload[0] == 0 ? 0f : (payload[0] - 0x40) / 63f;
                break;
            case 0x7F when payload.Length >= 1 && payload[0] == 0x00:
                // GS Reset: full state reset.
                Reset();
                break;
        }
    }

    private void HandleGsReverbChorus(int al, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return;
        var v = payload[0];
        switch (al)
        {
            // Reverb macros 0x30 = type (0=room1..7=plate), 0x31 = character, 0x32 = pre-LPF,
            // 0x33 = level, 0x34 = time, 0x35 = delay feedback. We map level/time to our Reverb.
            case 0x33: _reverb.Wet = v / 127f; break;
            case 0x34: _reverb.RoomSize = 0.3f + v / 127f * 0.6f; break;
            // Chorus macros 0x38..0x3F. 0x3A = level, 0x3D = depth.
            case 0x3A: _chorus.Level = v / 127f; break;
            // 0x30, 0x31, 0x32, 0x35, 0x38, 0x39, 0x3B, 0x3C, 0x3E, 0x3F: accepted, no mapping.
        }
    }

    private void HandleGsPart(int channel, int al, ReadOnlySpan<byte> payload)
    {
        if (channel < 0 || channel >= _channels.Length || payload.Length < 1) return;
        var ch = _channels[channel];
        var v = payload[0];
        switch (al)
        {
            case 0x15:  // Use For Rhythm Part: 0=off, 1=MAP1 (bank 128), 2=MAP2 (bank 127).
                ch.IsDrumPart = v != 0;
                ch.DrumBank = v == 2 ? (ushort)127 : (ushort)128;
                ResolveBank(ch);
                break;
            case 0x16:  // Pitch Key Shift: 0x28..0x58 = -24..+24 semitones (center 0x40)
                ch.CoarseTuneSemitones = (sbyte)Math.Clamp(v - 0x40, -64, 63);
                break;
            case 0x17:  // Pitch Offset Fine — 14-bit but only MSB sent here; rare.
                break;
            case 0x19:  // Part Level (volume)
                ch.Volume = v;
                break;
            case 0x1C:  // Part Pan: 0=random, 1..127 = left..right (0x40 center)
                if (v != 0) ch.Pan = v;
                break;
            case 0x21:  // Velocity Sense Depth (we don't apply; would scale velocity range)
            case 0x22:  // Velocity Sense Offset
                break;
        }
    }

    // Roland GS part block index → MIDI channel. Quirk: block 0 = part 10 (drums by default),
    // blocks 1..9 = parts 1..9 (channels 0..8), blocks A..F = parts 11..16 (channels 10..15).
    private static int GsPartBlockToChannel(int block)
    {
        return block switch
        {
            0 => 9,
            <= 9 => block - 1,
            _ => block
        };
    }

    // ---- Yamaha XG ----

    private void HandleYamahaXg(ReadOnlySpan<byte> d)
    {
        // 43 dev 4C ah am al data... (F7) — model 4C = XG.
        if (d.Length < 8 || d[2] != 0x4C) return;
        int ah = d[3], am = d[4], al = d[5];

        var dataEnd = d.Length - 1;
        if (d[dataEnd] == 0xF7) dataEnd--;
        var payload = d[6..(dataEnd + 1)];

        switch (ah)
        {
            // System area (ah=00 am=00): reset variants and master parameters.
            case 0x00 when am == 0x00:
            {
                switch (al)
                {
                    // XG System On: al=7E payload[0]=00 (already covered by Reset detection path).
                    case 0x7E when payload.Length >= 1 && payload[0] == 0x00:
                    // XG All Parameter Reset: al=7F payload[0]=00.
                    case 0x7F when payload.Length >= 1 && payload[0] == 0x00:
                        Reset(); return;
                    // Master Tune: al=00, four nibbles
                    case 0x00 when payload.Length >= 4:
                    {
                        var raw = ((payload[0] & 0x0F) << 12) | ((payload[1] & 0x0F) << 8)
                                                              | ((payload[2] & 0x0F) << 4) | (payload[3] & 0x0F);
                        _masterTuneCents = (raw - 0x0400) / 10.0;
                        return;
                    }
                    // Master Volume: al=04
                    case 0x04 when payload.Length >= 1:
                        _masterVolume = payload[0] / 127f; return;
                    // Master Pan: al=06
                    case 0x06 when payload.Length >= 1:
                        _masterPan = payload[0] == 0 ? 0f : (payload[0] - 0x40) / 63f; return;
                }

                break;
            }
            // Multi-part: ah=08 am=part al=parameter
            case 0x08 when am < 16 && payload.Length >= 1:
            {
                var ch = _channels[am];
                var v = payload[0];
                switch (al)
                {
                    case 0x07:  // Part Mode: 0=normal, 1=drum, 2=drum-S1, 3=drum-S2.
                        ch.IsDrumPart = v != 0;
                        // XG uses bank 127 for SFX kits (Setup 2/3); MAP1 for normal drum.
                        ch.DrumBank = v >= 2 ? (ushort)127 : (ushort)128;
                        ResolveBank(ch);
                        break;
                    case 0x0B:  // Volume
                        ch.Volume = v;
                        break;
                    case 0x0E:  // Pan
                        if (v != 0) ch.Pan = v;
                        break;
                }

                break;
            }
        }
    }

    // ---- Master state set by GS/XG/Universal SysEx ----

    private float _masterPan;            // -1..+1, applied as L/R balance in Generate
    private double _masterTuneCents;     // additive to every voice's pitch
    private int _masterKeyShiftSemitones;

    private float _masterVolume = 1.0f;

    /// <summary>
    /// Master volume scalar (0..1) set by Universal Master Volume SysEx. Default 1.0.
    /// </summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Stops all sound immediately.
    /// </summary>
    public void AllSoundOff()
    {
        foreach (var voice in _voices)
            voice.Kill();
    }

    /// <summary>
    /// Releases all notes on all channels.
    /// </summary>
    public void AllNotesOff()
    {
        foreach (var voice in _voices)
        {
            if (voice.State == VoiceState.Playing)
                voice.Release();
        }
    }

    /// <summary>
    /// Resets the synthesizer to initial state.
    /// </summary>
    public void Reset()
    {
        AllSoundOff();
        foreach (var channel in _channels)
            channel.ResetAll();

        // Reset channel 10 to percussion. Any GS "Use For Rhythm Part" SysEx that moved
        // drums elsewhere is now undone — only channel 10 is drums after a Reset.
        for (var i = 0; i < _channels.Length; i++)
        {
            var isDrum = i == 9;
            _channels[i].IsDrumPart = isDrum;
            _channels[i].DrumBank = 128;  // back to MAP1 default
            _channels[i].Bank = isDrum ? (ushort)128 : (ushort)0;
            _channels[i].DrumOverrides?.Clear();
        }

        _reverb.Reset();
        _chorus.Reset();
        _masterVolume = 1.0f;
        _masterPan = 0f;
        _masterTuneCents = 0;
        _masterKeyShiftSemitones = 0;
    }

    /// <summary>
    /// Generates audio samples into stereo buffers.
    /// </summary>
    /// <param name="left">Left channel buffer</param>
    /// <param name="right">Right channel buffer</param>
    public void Generate(Span<float> left, Span<float> right)
    {
        var frames = left.Length;

        // Clear output buffers
        left.Clear();
        right.Clear();

        // Ensure send buffers are sized for this call, then clear them
        if (_reverbSendBuffer.Length < frames)
        {
            _reverbSendBuffer = new float[frames];
            _chorusSendBuffer = new float[frames];
        }
        var reverbSend = _reverbSendBuffer.AsSpan(0, frames);
        var chorusSend = _chorusSendBuffer.AsSpan(0, frames);
        reverbSend.Clear();
        chorusSend.Clear();

        // Process each voice — writes dry signal to L/R AND mono signal to send buses
        foreach (var voice in _voices)
        {
            if (voice.State == VoiceState.Free)
                continue;

            var channelState = _channels[voice.Channel];

            // SF2 default modulators 9 & 10: CC 91/93 contribute amount=200 (0.1% units)
            // → 0.2 fraction max — added on top of the voice's own ReverbSend/ChorusSend.
            var channelReverbContrib = 0.2f * (channelState.ReverbSendCc / 127f);
            var channelChorusContrib = 0.2f * (channelState.ChorusSendCc / 127f);

            // SF2 default modulators 3 & 4: channel pressure and CC1 (mod wheel) each add
            // up to ModulationDepthRangeCents (default 50, RPN 0,5 settable) of vibrato
            // LFO pitch depth at full value, linear-unipolar. Both default modulators
            // target the same destination so they sum independently — full mod wheel +
            // full aftertouch can drive vibrato depth to 2× the range on top of the
            // patch's own VibLfoToPitch.
            var channelVibDepthCents =
                (channelState.Modulation / 127.0 + channelState.ChannelPressure / 127.0)
                * channelState.ModulationDepthRangeCents;

            // GM2 CC 77 vibrato depth: bipolar around 64, adds ±50 cents independently
            // of mod wheel / aftertouch.
            channelVibDepthCents += (channelState.VibratoDepthCc - 64) * (50.0 / 64.0);

            // CC 10 pan: 0=hard left, 64=center, 127=hard right. SF2 _pan is in -500..500
            // units, so scale CC10 the same way. The voice then clamps the sum, so a
            // hard-panned SF2 patch with extra CC10 in the same direction stays at hard pan.
            var channelPan = (channelState.Pan - 64) * (500.0 / 63.0);

            // CC 67 (soft pedal / una corda): ≥ 64 applies ~6 dB attenuation = 60 cB.
            // GM2 RP-021 doesn't fix a number; 6 dB matches what most software synths use
            // and is audible without being dramatic.
            var softPedalAttenuationCb = channelState.SoftPedal >= 64 ? 60.0 : 0.0;

            // CC 92 tremolo: sin LFO modulates voice attenuation symmetrically up to
            // ±TremoloMaxAttenCb at full depth. Phase is advanced once per Generate call.
            var tremoloAttenCb = 0.0;
            if (channelState.TremoloDepth > 0)
            {
                var depth = channelState.TremoloDepth / 127.0;
                // sin returns -1..+1; map to 0..1 then to attenuation (no negative gain).
                var lfo = 0.5 * (1.0 - Math.Sin(_tremoloPhase[voice.Channel]));
                tremoloAttenCb = lfo * depth * TremoloMaxAttenCb;
            }

            // CC 8 Balance: 0=mute left, 64=unity, 127=mute right. Applied as additional
            // per-channel L/R gain multipliers inside Voice.Process after the pan calc.
            var balanceLeft = channelState.Balance >= 64 ? 1f : channelState.Balance / 64f;
            var balanceRight = channelState.Balance <= 64 ? 1f : (127 - channelState.Balance) / 63f;

            // Master tune (cents) and master key shift (semitones) from GS/XG SysEx are
            // additive to every voice's pitch offset.
            var pitchOffset = channelState.TotalPitchOffsetCents
                              + _masterTuneCents
                              + _masterKeyShiftSemitones * 100.0;

            voice.Process(
                left,
                right,
                reverbSend,
                chorusSend,
                pitchOffset,
                channelState.VolumeNormalized,
                channelState.ExpressionNormalized,
                Math.Max(GlobalReverbSend, channelReverbContrib),
                Math.Max(GlobalChorusSend, channelChorusContrib),
                channelVibDepthCents,
                channelPan,
                softPedalAttenuationCb + tremoloAttenCb,
                channelState.FilterCutoffOffsetCents,
                balanceLeft,
                balanceRight);

            // Check if voice finished
            if (voice.IsFinished)
                voice.Reset();
        }

        // Advance each channel's tremolo phase by one buffer's worth of time.
        var tremoloStep = 2.0 * Math.PI * TremoloFrequencyHz * frames / _sampleRate;
        for (var c = 0; c < _tremoloPhase.Length; c++)
        {
            if (_channels[c].TremoloDepth <= 0) continue;
            _tremoloPhase[c] += tremoloStep;
            if (_tremoloPhase[c] > 2.0 * Math.PI) _tremoloPhase[c] -= 2.0 * Math.PI;
        }

        // Mix wet effect output back into the main L/R buffer.
        _reverb.Process(reverbSend, left, right);
        _chorus.Process(chorusSend, left, right);

        // Master volume + master pan applied as a final stage after the effect mix.
        // Master pan is a simple balance: attenuate the opposite channel.
        var needScale = Math.Abs(_masterVolume - 1.0f) > 0.001f || _masterPan != 0f;
        if (!needScale) return;
        var mv = _masterVolume;
        var leftMul = mv * (_masterPan > 0 ? 1f - _masterPan : 1f);
        var rightMul = mv * (_masterPan < 0 ? 1f + _masterPan : 1f);
        for (var i = 0; i < frames; i++)
        {
            left[i] *= leftMul;
            right[i] *= rightMul;
        }
    }

    /// <summary>
    /// Generates audio samples into an interleaved stereo buffer.
    /// </summary>
    public void GenerateInterleaved(Span<float> buffer)
    {
        var frames = buffer.Length / 2;

        // Ensure temp buffers are large enough
        if (_leftBuffer.Length < frames)
        {
            _leftBuffer = new float[frames];
            _rightBuffer = new float[frames];
        }

        var left = _leftBuffer.AsSpan(0, frames);
        var right = _rightBuffer.AsSpan(0, frames);

        Generate(left, right);

        // Interleave
        for (var i = 0; i < frames; i++)
        {
            buffer[i * 2] = left[i];
            buffer[i * 2 + 1] = right[i];
        }
    }

    private Voice? AllocateVoice(int channel, int key)
    {
        // First, look for a free voice
        foreach (var voice in _voices)
        {
            if (voice.State == VoiceState.Free)
                return voice;
        }

        // Voice stealing: find the oldest released voice
        Voice? oldest = null;
        var oldestGen = int.MaxValue;

        foreach (var voice in _voices)
        {
            if (voice.State != VoiceState.Released || voice.GenerationId >= oldestGen) continue;
            oldest = voice;
            oldestGen = voice.GenerationId;
        }

        if (oldest != null)
        {
            oldest.Kill();
            return oldest;
        }

        // Last resort: steal the oldest playing voice
        foreach (var voice in _voices)
        {
            if (voice.GenerationId >= oldestGen) continue;
            oldest = voice;
            oldestGen = voice.GenerationId;
        }

        if (oldest == null) return null;
        oldest.Kill();
        return oldest;

    }

    private void KillVoicesByChannelKey(int channel, int key)
    {
        foreach (var voice in _voices)
        {
            if (voice.State != VoiceState.Free &&
                voice.Channel == channel &&
                voice.KeyNumber == key)
            {
                voice.Kill();
            }
        }
    }

    private void KillVoicesByExclusiveClass(int channel, int exclusiveClass)
    {
        foreach (var voice in _voices)
        {
            if (voice.State != VoiceState.Free &&
                voice.Channel == channel &&
                voice.ExclusiveClass == exclusiveClass)
            {
                voice.Kill();
            }
        }
    }

    private void ReleaseAllVoices(int channel)
    {
        foreach (var voice in _voices)
        {
            if (voice.State == VoiceState.Playing && voice.Channel == channel)
                voice.Release();
        }
    }

    private void ReleaseAllSustainedVoices(int channel)
    {
        foreach (var voice in _voices)
        {
            if (voice.State != VoiceState.Playing || voice.Channel != channel) continue;
            // Sostenuto still down on a captured voice → defer release until sostenuto lifts.
            if (voice.SostenutoHeld)
                voice.SostenutoReleasePending = true;
            else
                voice.Release();
        }
    }

    private void KillAllVoices(int channel)
    {
        foreach (var voice in _voices)
        {
            if (voice.Channel == channel)
                voice.Kill();
        }
    }
}
