using System;
using System.Collections.Generic;
using MidiSharp.SoundBank;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Synth;

/// <summary>
/// SoundFont-based software synthesizer.
/// Manages voice allocation, MIDI processing, and audio generation.
/// </summary>
public sealed class Synthesizer
{
    private const int MaxVoices = 512;
    private const int ChannelCount = 16;

    /// <summary>
    /// Synthetic bank under which a voice's mixer identity is keyed by its source MIDI track (a note's
    /// mix id becomes (<see cref="TrackMixBank"/>, trackIndex)). Lets the mixer group by part/track
    /// rather than by sound, so a performer keeps one fader even as their program changes — and two
    /// tracks sharing a program get separate faders. Far above any real or track-override bank so it
    /// never collides. A note with no track (trackIndex &lt; 0, e.g. live input) falls back to keying
    /// by its resolved (bank, program).
    /// </summary>
    public const int TrackMixBank = 2_000_000;

    private readonly Voice[] _voices;
    private readonly ChannelState[] _channels;
    private readonly int _sampleRate;
    private int _generationCounter;

    // SFZ random round-robin (lorand/hirand). Fixed seed → identical note selections
    // for the same event sequence, so renders stay reproducible for A/B comparison.
    private readonly Random _rng = new(0x5F2_A12A);

    private IRBank? _soundBank;

    // Per-track instrument routing: trackIndex → the synthetic (bank, program) its forced patch
    // lives at in the loaded composite. Empty by default (notes resolve by channel as usual).
    private IReadOnlyDictionary<int, (int Bank, int Program)> _trackPatchMap = EmptyTrackPatchMap;
    private static readonly IReadOnlyDictionary<int, (int Bank, int Program)> EmptyTrackPatchMap
        = new Dictionary<int, (int Bank, int Program)>();

    // Tier-1 per-instrument mixer trims, keyed by the (bank, program) a note's patch resolves to.
    // Empty by default — while empty the voice loop takes the exact pre-mixer path (Generate gates on
    // Count), so playback is bit-identical until a control is touched. Entries are created lazily and
    // mutated in place so a live fader move is heard by notes already sounding.
    private readonly Dictionary<InstrumentId, InstrumentMix> _instrumentMixes = new();
    private bool _anySolo;   // recomputed once per Generate when any mix exists

    // Tier-2 per-instrument insert effects (host-supplied DSP). Published as an immutable snapshot and
    // read once per Generate, so a live add/remove from the control thread never tears the audio read.
    // Empty by default → Generate takes the exact pre-Tier-2 path (no buses), bit-identical.
    private volatile IReadOnlyDictionary<InstrumentId, IInstrumentInsert> _instrumentInserts = EmptyInserts;
    private static readonly IReadOnlyDictionary<InstrumentId, IInstrumentInsert> EmptyInserts
        = new Dictionary<InstrumentId, IInstrumentInsert>();
    // Private per-instrument bus buffers, allocated lazily for instruments that have an insert. Owned
    // solely by the audio thread (only Generate touches it), so it needs no synchronization.
    private readonly Dictionary<InstrumentId, InstrumentBus> _buses = new();

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
    private const double TremoloMaxAttenDb = 6.0;  // ±6 dB at CC92=127

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
    /// Gets the currently loaded sound bank, or null if none has been loaded.
    /// </summary>
    public IRBank? SoundBank => _soundBank;

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
    /// Installs a pre-loaded <see cref="IRBank"/>. Replaces any current bank;
    /// the old one is disposed only when the caller does so explicitly (the synth
    /// doesn't own banks it didn't load).
    /// </summary>
    public void LoadSoundFont(IRBank soundBank)
    {
        if (soundBank == null) throw new ArgumentNullException(nameof(soundBank));

        AllSoundOff();
        _soundBank = soundBank;

        // SFZ sustain_cc reassignment: tell every channel which controller acts as its sustain pedal
        // (CC64 for SF2/SF3/DLS and most SFZ; a half-pedal font may route it elsewhere, e.g. CC90).
        for (var ch = 0; ch < _channels.Length; ch++)
            _channels[ch].SustainCc = soundBank.SustainCc;

        // Seed the instrument's expected initial controller values (SFZ set_ccN/set_hdccN) onto every
        // channel so CC-driven routes and locc/hicc gates start where the bank expects. Routed through
        // ControlChange so known CCs hit their fields and the rest land in the generic store.
        if (soundBank.InitialControllers.Count > 0)
            for (var ch = 0; ch < _channels.Length; ch++)
                foreach (var kv in soundBank.InitialControllers)
                    ControlChange(ch, kv.Key, kv.Value);
    }

    /// <summary>
    /// Installs a per-track instrument routing map (trackIndex → synthetic (bank, program) in the
    /// loaded composite, as produced by <c>SoundBankComposer.BuildComposite</c>). A NoteOn that
    /// carries a track index present in this map resolves to the mapped patch instead of the
    /// channel's program — letting one part be forced to an instrument independent of the
    /// channel/program its notes carry. Pass an empty map (or none) to route purely by channel.
    /// </summary>
    public void SetTrackPatchMap(IReadOnlyDictionary<int, (int Bank, int Program)> map)
        => _trackPatchMap = map ?? EmptyTrackPatchMap;

    /// <summary>
    /// The live per-instrument mixer trims, keyed by the (bank, program) a note's patch resolves to.
    /// Read-only view; obtain a mutable entry with <see cref="GetInstrumentMix"/>. Empty until a mix
    /// is first requested — while empty, playback is bit-identical to the pre-mixer engine.
    /// </summary>
    public IReadOnlyDictionary<InstrumentId, InstrumentMix> InstrumentMixes => _instrumentMixes;

    /// <summary>
    /// Returns the mutable <see cref="InstrumentMix"/> trim for an instrument, creating a no-op entry
    /// on first request. Edit the returned object live (gain/pan/mute/solo/sends) — notes already
    /// sounding for that instrument pick the change up on their next audio block. The default entry is
    /// a true no-op, so requesting (and not changing) a mix leaves playback bit-identical.
    /// </summary>
    public InstrumentMix GetInstrumentMix(int bank, int program)
    {
        var id = new InstrumentId(bank, program);
        if (!_instrumentMixes.TryGetValue(id, out var mix))
        {
            mix = new InstrumentMix();
            _instrumentMixes[id] = mix;
        }
        return mix;
    }

    /// <summary>Gets an existing instrument mix without creating one. Returns false if untouched.</summary>
    public bool TryGetInstrumentMix(int bank, int program, out InstrumentMix mix)
        => _instrumentMixes.TryGetValue(new InstrumentId(bank, program), out mix!);

    /// <summary>Removes all per-instrument mixer trims, returning the engine to its bit-identical
    /// pre-mixer path.</summary>
    public void ClearInstrumentMixes() => _instrumentMixes.Clear();

    /// <summary>
    /// Registers (or, with a null <paramref name="insert"/>, removes) a per-instrument insert effect
    /// for the instrument at (<paramref name="bank"/>, <paramref name="program"/>). The instrument's
    /// voices are then summed into a private bus, run through <paramref name="insert"/>, and mixed to
    /// master. Thread-safe vs. the audio thread (publishes a new snapshot). Instruments without an
    /// insert are unaffected and stay bit-identical.
    /// </summary>
    public void SetInstrumentInsert(int bank, int program, IInstrumentInsert? insert)
    {
        var id = new InstrumentId(bank, program);
        var copy = new Dictionary<InstrumentId, IInstrumentInsert>(_instrumentInserts);
        if (insert == null) copy.Remove(id); else copy[id] = insert;
        _instrumentInserts = copy;
    }

    /// <summary>Removes all per-instrument inserts.</summary>
    public void ClearInstrumentInserts() => _instrumentInserts = EmptyInserts;

    /// <summary>The currently registered per-instrument inserts (read-only snapshot).</summary>
    public IReadOnlyDictionary<InstrumentId, IInstrumentInsert> InstrumentInserts => _instrumentInserts;

    /// <summary>
    /// Processes a MIDI note on event.
    /// </summary>
    public void NoteOn(int channel, int key, int velocity) => NoteOn(channel, key, velocity, -1);

    /// <summary>
    /// Processes a MIDI note on event originating from <paramref name="trackIndex"/>. When the
    /// track has a per-track override (see <see cref="SetTrackPatchMap"/>) the note is forced to
    /// that instrument; otherwise it resolves by channel program exactly as the 3-arg overload.
    /// Pass <c>-1</c> for <paramref name="trackIndex"/> when the source track is unknown.
    /// </summary>
    public void NoteOn(int channel, int key, int velocity, int trackIndex)
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

        if (_soundBank == null) return;

        var channelState = _channels[channel];

        // Per-track override wins: a note from a mapped track is forced to its instrument,
        // ignoring the channel's program. Falls through to channel resolution if unmapped or
        // (defensively) if the synthetic patch is somehow absent.
        Patch? patch = null;
        if (trackIndex >= 0 && _trackPatchMap.TryGetValue(trackIndex, out var addr))
            patch = _soundBank.FindPatch(addr.Bank, addr.Program);
        patch ??= _soundBank.FindPatch(channelState.Bank, channelState.Program)
                  ?? _soundBank.FindPatch(0, channelState.Program);
        if (patch == null) return;

        // SFZ keyswitch: a key inside a zone's switch range selects an articulation
        // for this channel and sounds no note of its own.
        if (TrySelectKeyswitch(patch, key, channelState)) return;

        // Determine portamento source (key we should glide from). CC 84 (one-shot) wins
        // over LastNoteKey when set, and falls back only when CC 65 (portamento on) is true.
        var portamentoSource = channelState.PortamentoSourceKey >= 0
            ? channelState.PortamentoSourceKey
            : channelState.PortamentoOn ? channelState.LastNoteKey : -1;
        var portamentoStartCents = 0.0;
        var portamentoTimeSeconds = 0.0;
        if (portamentoSource >= 0 && portamentoSource != key)
        {
            portamentoStartCents = (portamentoSource - key) * 100.0;
            // CC 5 0..127 mapped to ~0..6 seconds via quadratic curve so low values are short.
            var t = channelState.PortamentoTimeCc / 127.0;
            portamentoTimeSeconds = t * t * 6.0;
        }
        channelState.PortamentoSourceKey = -1;
        channelState.LastNoteKey = (sbyte)key;

        // GM2 sound-controller offsets in octaves (±1 octave at CC=0/127, 0 at CC=64).
        var attackOctaves = (channelState.AttackTimeCc - 64) / 64.0;
        var decayOctaves = (channelState.DecayTimeCc - 64) / 64.0;
        var releaseOctaves = (channelState.ReleaseTimeCc - 64) / 64.0;
        var vibFreqOctaves = (channelState.VibratoRateCc - 64) / 64.0;
        var vibDelayOctaves = (channelState.VibratoDelayCc - 64) / 64.0;
        var filterQDbOffset = channelState.FilterQOffsetCb / 10.0;

        var samples = _soundBank.Samples;

        // Round-robin sequence index for this NoteOn, computed once (lazily, only
        // if a round-robin zone is actually reached) and shared across zones.
        int? rrIndex = null;

        // Random round-robin roll for this NoteOn — one value shared by every zone so
        // a set of lorand/hirand ranges tiling [0,1) selects exactly one. Lazy: the
        // RNG only advances when a random zone is actually evaluated, so non-random
        // banks leave the stream (and reproducibility) untouched.
        double? roll = null;

        foreach (var zone in patch.Zones)
        {
            if (!zone.Keys.Contains(key)) continue;
            if (!zone.Velocities.Contains(velocity)) continue;
            // Release zones fire on NoteOff (the damper/string-release samples), not here. Skipping
            // them is what stops SFZ release samples from layering onto every key-down; they're spawned
            // from NoteOff instead. (First/Legato are NoteOn triggers, so they still sound here.)
            if (zone.Trigger == ZoneTrigger.Release) continue;
            // CC-gated zones (SFZ; empty for SF2/SF3/DLS).
            if (zone.CCGates.Count > 0 && !PassesCCGates(zone.CCGates, channelState)) continue;

            // SFZ keyswitch: zone is active only when the channel's selected switch
            // key (or this zone's default, before any switch was pressed) matches.
            if (zone.KeySwitch is { } ks)
            {
                int selected = channelState.SelectedKeyswitch >= 0 ? channelState.SelectedKeyswitch : ks.Default;
                if (selected != ks.SelectingKey) continue;
            }

            // SFZ round-robin: rotate seq_position zones across successive NoteOns.
            if (zone.RoundRobin is { } rr && rr.Length > 1)
            {
                rrIndex ??= channelState.NextRoundRobin(key);
                if (rrIndex.Value % rr.Length != rr.Position) continue;
            }

            // SFZ random round-robin: zone sounds only if this note's roll is in range.
            if (zone.Random is { } rand)
            {
                roll ??= _rng.NextDouble();
                if (roll.Value < rand.Lo || roll.Value >= rand.Hi) continue;
            }

            var voice = AllocateVoice(channel, key);
            if (voice == null) continue;

            // Exclusive group: silence any sounding voice in the same group on the channel.
            if (zone.ExclusiveGroup is { } eg && eg > 0)
                KillVoicesByExclusiveClass(channel, eg);

            voice.Configure(zone, samples, key, velocity, channel, ++_generationCounter, channelState);

            // Tag the voice with its mixer instrument id. With a known source track we key by the TRACK
            // (so the mixer groups by part — one fader per performer, stable across program changes, and
            // two tracks sharing a program stay separate). Without a track (live input) we fall back to
            // the resolved (bank, program). The source patch — native or a per-track/patch override — is
            // independent of this mix id.
            voice.Instrument = trackIndex >= 0
                ? new InstrumentId(TrackMixBank, trackIndex)
                : new InstrumentId(patch.Bank, patch.Program);

            // SFZ polyphony: cap simultaneous voices from this region, stealing the oldest past the limit
            // (the just-allocated voice has the highest generation, so it survives unless the cap is 0).
            if (zone.Polyphony >= 0)
                EnforceRegionPolyphony(zone, channel);

            // GM2 CC72/73/75 envelope time scaling.
            if (attackOctaves != 0 || decayOctaves != 0 || releaseOctaves != 0)
                voice.ApplyEnvelopeTimeScaling(attackOctaves, decayOctaves, releaseOctaves);

            // CC 71 Q offset baked at NoteOn — live CC 71 changes don't retrofit.
            if (filterQDbOffset != 0)
                voice.ApplyExtraResonance(filterQDbOffset);

            if (portamentoStartCents != 0.0 && portamentoTimeSeconds > 0.0)
                voice.StartPortamento(portamentoStartCents, portamentoTimeSeconds);

            if (vibFreqOctaves != 0 || vibDelayOctaves != 0)
                voice.AdjustVibratoLfo(vibFreqOctaves, vibDelayOctaves);

            // GS drum NRPN overrides for this specific (channel, key).
            if (channelState.DrumOverrides != null &&
                channelState.DrumOverrides.TryGetValue(key, out var drumOv))
                voice.ApplyDrumOverride(drumOv);

            // SFZ humanization (amp/pitch/delay/offset random + fixed delay). Rolled from the synth's
            // seeded RNG so each note varies but renders stay reproducible. Only touch the RNG when the
            // zone actually asks for variation, so banks without it keep the exact RNG stream (and the
            // byte-identical renders) they have today. Each draw is uniform [0, value] (sfizz's law).
            if (zone.AmpRandomDb != 0 || zone.PitchRandomCents != 0 || zone.FilterRandomCents != 0 ||
                zone.DelaySeconds != 0 || zone.DelayRandomSeconds != 0 || zone.OffsetRandomFrames != 0)
            {
                var gainDb = zone.AmpRandomDb != 0 ? _rng.NextDouble() * zone.AmpRandomDb : 0.0;
                var detune = zone.PitchRandomCents != 0 ? _rng.NextDouble() * zone.PitchRandomCents : 0.0;
                var delaySec = zone.DelaySeconds
                               + (zone.DelayRandomSeconds != 0 ? _rng.NextDouble() * zone.DelayRandomSeconds : 0.0);
                var offFrames = zone.OffsetRandomFrames != 0 ? (long)(_rng.NextDouble() * zone.OffsetRandomFrames) : 0;
                var filRand = zone.FilterRandomCents != 0 ? _rng.NextDouble() * zone.FilterRandomCents : 0.0;
                voice.ApplyHumanization(gainDb, detune, delaySec, offFrames, filRand);
            }
        }
    }

    private static bool PassesCCGates(IReadOnlyList<CCGate> gates, ChannelState ch)
    {
        for (var i = 0; i < gates.Count; i++)
        {
            var gate = gates[i];
            if (!gate.Contains(ch.GetCC(gate.Controller))) return false;
        }
        return true;
    }

    /// <summary>
    /// SFZ keyswitching: if <paramref name="key"/> falls inside any zone's
    /// keyswitch range, record it as the channel's selected switch and return true
    /// (the caller then sounds no note). Cheap no-op for banks without keyswitches.
    /// </summary>
    private static bool TrySelectKeyswitch(Patch patch, int key, ChannelState ch)
    {
        var zones = patch.Zones;
        for (var i = 0; i < zones.Count; i++)
        {
            if (zones[i].KeySwitch is { } ks && key >= ks.Low && key <= ks.High)
            {
                ch.SelectedKeyswitch = (sbyte)key;
                return true;
            }
        }
        return false;
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

        // Sustain pedal — handled here (not as a switch case) because SFZ sustain_cc can reassign it to
        // any controller. Also stored generically so routes/ampeg_dynamic can read the raw value. When
        // the bank keeps the default (CC64), this is exactly the old behaviour.
        if (controller == channelState.SustainCc)
        {
            var on = value >= 64;
            if (channelState.Sustain && !on)
                ReleaseAllSustainedVoices(channel);
            channelState.Sustain = on;
            channelState.SetGenericCc(controller, (byte)value);
            return;
        }

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

            default:
                // Any controller without dedicated handling (e.g. SFZ-modulated CC20/22/99) is stored
                // generically so locc/hicc gates and CC routes can read it.
                channelState.SetGenericCc(controller, (byte)Math.Clamp(value, 0, 127));
                break;
        }
    }

    // Delegates to the shared, pure BankResolution helper so offline patch-usage analysis
    // resolves banks identically to playback. Behavior is unchanged from the inline version.
    private static void ResolveBank(ChannelState ch)
        => ch.Bank = (ushort)BankResolution.Resolve(ch.BankMsb, ch.BankLsb, ch.IsDrumPart, ch.DrumBank);

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
            ch.DrumOverrides ??= new Dictionary<int, DrumKeyOverride>();
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
    /// Processes a MIDI polyphonic aftertouch event. Stores the raw 0..127
    /// value on the matching voice; the route evaluator reads it on the next
    /// block when SF2 default modulator #5 (PolyPressure → VibratoLfoPitchDepth)
    /// or any other zone-authored route references the PolyPressure source.
    /// </summary>
    public void PolyPressure(int channel, int key, int pressure)
    {
        if ((uint)channel >= ChannelCount) return;
        if ((uint)key >= 128) return;
        var clamped = (byte)Math.Clamp(pressure, 0, 127);

        foreach (var voice in _voices)
        {
            if (voice.State != VoiceState.Free &&
                voice.Channel == channel &&
                voice.KeyNumber == key)
            {
                voice.PolyPressure = clamped;
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

        // Tier-1 mixer: only do any per-instrument work when at least one trim exists. While the map is
        // empty this whole layer is skipped and the voice loop is the exact pre-mixer code path.
        var mixerActive = _instrumentMixes.Count != 0;
        if (mixerActive)
        {
            _anySolo = false;
            foreach (var m in _instrumentMixes.Values)
                if (m.Solo) { _anySolo = true; break; }
        }

        // Tier-2 inserts: read the immutable snapshot once (stable for this block). When any exist,
        // instruments with an insert render into a private bus instead of straight into master.
        var inserts = _instrumentInserts;
        var hasInserts = inserts.Count != 0;
        if (hasInserts) PrepareBuses(inserts, frames);

        // Process each voice — writes dry signal to L/R AND mono signal to send buses
        foreach (var voice in _voices)
        {
            if (voice.State == VoiceState.Free)
                continue;

            var channelState = _channels[voice.Channel];

            // Extras outside the route framework: things that aren't standard
            // modulators or that don't fit the unipolar/bipolar source model.

            // GM2 CC 77 (vibrato depth): bipolar around 64, ±50 cents. The route
            // framework uses unipolar sources only, so this stays synth-level.
            var extraVibCents = (channelState.VibratoDepthCc - 64) * (50.0 / 64.0);

            // CC 67 (soft pedal / una corda): ≥ 64 = ~6 dB attenuation.
            var softPedalAttenDb = channelState.SoftPedal >= 64 ? 6.0 : 0.0;

            // CC 92 tremolo: sin LFO modulates attenuation symmetrically up to
            // ±TremoloMaxAttenDb at full depth. Phase advances once per Generate.
            var tremoloAttenDb = 0.0;
            if (channelState.TremoloDepth > 0)
            {
                var depth = channelState.TremoloDepth / 127.0;
                var lfo = 0.5 * (1.0 - Math.Sin(_tremoloPhase[voice.Channel]));
                tremoloAttenDb = lfo * depth * TremoloMaxAttenDb;
            }

            // CC 8 Balance: post-pan L/R gain multipliers.
            var balanceLeft = channelState.Balance >= 64 ? 1f : channelState.Balance / 64f;
            var balanceRight = channelState.Balance <= 64 ? 1f : (127 - channelState.Balance) / 63f;

            // Non-bend channel-level pitch offset: master tune + master key shift
            // + RPN 0,1 fine tune + RPN 0,2 coarse tune. The pitch-bend portion is
            // handled by the modulation route #10 inside the voice.
            var nonBendPitchCents =
                channelState.FineTuneCents
                + channelState.CoarseTuneSemitones * 100.0
                + _masterTuneCents
                + _masterKeyShiftSemitones * 100.0;

            // Per-instrument mixer trim. gain → an attenuation offset (rides on top of CC7/CC11);
            // pan/sends → additive offsets; mute/solo → a linear gate factor. All no-ops when the
            // instrument has no entry, so untouched instruments render bit-identically.
            double instAttenDb = 0.0, instPan = 0.0;
            float instReverbAdd = 0f, instChorusAdd = 0f, instGainFactor = 1f;
            if (mixerActive)
            {
                _instrumentMixes.TryGetValue(voice.Instrument, out var mix);
                if (mix != null)
                {
                    instAttenDb = -mix.GainDb;
                    instPan = mix.Pan;
                    instReverbAdd = (float)mix.ReverbSend;
                    instChorusAdd = (float)mix.ChorusSend;
                }
                // Solo silences every non-soloed instrument (including ones with no entry); mute
                // silences unconditionally.
                if (_anySolo && mix is not { Solo: true }) instGainFactor = 0f;
                if (mix is { Mute: true }) instGainFactor = 0f;
            }

            // Tier-2: route this voice's dry signal to its instrument's private bus when that
            // instrument has an insert; otherwise straight to master (the pre-Tier-2 path). The
            // reverb/chorus sends stay global — taken pre-insert, as today.
            Span<float> outL = left, outR = right;
            if (hasInserts && inserts.ContainsKey(voice.Instrument))
            {
                var bus = _buses[voice.Instrument];
                outL = bus.L.AsSpan(0, frames);
                outR = bus.R.AsSpan(0, frames);
            }

            voice.Process(
                outL,
                outR,
                reverbSend,
                chorusSend,
                channelState,
                extraAttenuationDb: softPedalAttenDb + tremoloAttenDb + instAttenDb,
                nonBendPitchCents: nonBendPitchCents,
                balanceLeft: balanceLeft,
                balanceRight: balanceRight,
                extraVibLfoDepthCents: extraVibCents,
                globalReverbFloor: GlobalReverbSend,
                globalChorusFloor: GlobalChorusSend,
                instrumentPanOffset: instPan,
                instrumentReverbAdd: instReverbAdd,
                instrumentChorusAdd: instChorusAdd,
                instrumentGainFactor: instGainFactor);

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

        // Tier-2: run each instrument's insert on its private bus, then sum the result into master.
        // Done before the global reverb/chorus so the wet tails sit on top of the inserted dry signal.
        if (hasInserts) MixBusesToMaster(inserts, left, right, frames);

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
                // SFZ note_selfmask=off: overlapping same-key notes ring together (plucked/struck), so
                // the previous strike is left untouched instead of cut.
                if (!voice.NoteSelfMask) continue;
                // SFZ note_polyphony/off_mode: fade the previous strike (so a trill overlaps naturally)
                // rather than hard-cutting it. Plain SF2/SFZ voices keep the abrupt kill. TurnOff only
                // affects a Playing voice, so earlier fading retriggers ring on undisturbed.
                if (voice.SmoothOff) voice.TurnOff();
                else voice.Kill();
            }
        }
    }

    private void KillVoicesByExclusiveClass(int channel, int exclusiveGroup)
    {
        foreach (var voice in _voices)
        {
            if (voice.State != VoiceState.Free &&
                voice.Channel == channel &&
                voice.ExclusiveGroup == exclusiveGroup)
            {
                if (voice.SmoothOff) voice.TurnOff();
                else voice.Kill();
            }
        }
    }

    /// <summary>
    /// SFZ polyphony: enforce a per-region voice cap. Counts the sounding voices from this exact zone on
    /// the channel and steals the oldest (lowest generation) until at most <c>zone.Polyphony</c> remain.
    /// The voice just allocated has the newest generation, so it survives unless the cap is 0.
    /// </summary>
    private void EnforceRegionPolyphony(PatchZone zone, int channel)
    {
        var cap = zone.Polyphony;
        // Count only Playing voices (a stolen voice becomes Released/Free and drops out, so the loop
        // terminates). This caps the simultaneously-held notes from the region; release tails ring on.
        while (true)
        {
            var count = 0;
            Voice? oldest = null;
            foreach (var voice in _voices)
            {
                if (voice.State != VoiceState.Playing || voice.Channel != channel ||
                    !ReferenceEquals(voice.Zone, zone)) continue;
                count++;
                if (oldest == null || voice.GenerationId < oldest.GenerationId) oldest = voice;
            }
            if (count <= cap || oldest == null) break;
            if (oldest.SmoothOff) oldest.TurnOff();
            else oldest.Kill();
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

    // Ensure a sized, cleared private bus exists for every instrument that currently has an insert.
    // Reuses buffers across blocks; stale buses (for instruments whose insert was removed) are left
    // alone — they're never read, since routing and MixBusesToMaster both key off the insert snapshot.
    private void PrepareBuses(IReadOnlyDictionary<InstrumentId, IInstrumentInsert> inserts, int frames)
    {
        foreach (var id in inserts.Keys)
        {
            if (!_buses.TryGetValue(id, out var bus))
            {
                bus = new InstrumentBus(frames);
                _buses[id] = bus;
            }
            else bus.EnsureSize(frames);
            bus.Clear(frames);
        }
    }

    // For each instrument with an insert: interleave its bus, run the insert in place, then sum the
    // result into master L/R. A bus with no voices this block is all-zeros and contributes nothing.
    private void MixBusesToMaster(IReadOnlyDictionary<InstrumentId, IInstrumentInsert> inserts,
        Span<float> left, Span<float> right, int frames)
    {
        foreach (var kv in inserts)
        {
            if (!_buses.TryGetValue(kv.Key, out var bus)) continue;
            var interleaved = bus.Interleaved;
            for (var i = 0; i < frames; i++)
            {
                interleaved[i * 2] = bus.L[i];
                interleaved[i * 2 + 1] = bus.R[i];
            }
            kv.Value.Process(interleaved.AsSpan(0, frames * 2));
            for (var i = 0; i < frames; i++)
            {
                left[i] += interleaved[i * 2];
                right[i] += interleaved[i * 2 + 1];
            }
        }
    }

    // A private stereo bus for one instrument's voices: separate L/R the voices add into, plus an
    // interleaved scratch for the insert (which takes interleaved stereo). Grows if the block does.
    private sealed class InstrumentBus
    {
        public float[] L;
        public float[] R;
        public float[] Interleaved;

        public InstrumentBus(int frames)
        {
            L = new float[frames];
            R = new float[frames];
            Interleaved = new float[frames * 2];
        }

        public void EnsureSize(int frames)
        {
            if (L.Length >= frames) return;
            L = new float[frames];
            R = new float[frames];
            Interleaved = new float[frames * 2];
        }

        public void Clear(int frames)
        {
            Array.Clear(L, 0, frames);
            Array.Clear(R, 0, frames);
        }
    }
}
