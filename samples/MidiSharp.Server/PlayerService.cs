using MidiSharp.Hosting;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.Sandbox;
using MidiSharp.IO;
using MidiSharp.Loader;
using MidiSharp.Model;
using MidiSharp.Model.Events;
using MidiSharp.PatchMap;
using MidiSharp.Synth;
using MidiSharp.Synth.OwnAudio;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Server;

public enum PlayerState { Idle, Playing, Completed }

public sealed record DeviceDto(string Id, string Name, string Engine, bool IsDefault);
public sealed record PatchOverrideDto(int LogicalBank, int LogicalProgram, string SourcePath, int SourceBank, int SourceProgram, double GainDb = 0);
public sealed record TrackOverrideDto(int TrackIndex, string? TrackName, string SourcePath, int SourceBank, int SourceProgram, double GainDb = 0);
// A per-part substitution: force the part (track, channel) to a source font's instrument. Routes one
// channel of a (possibly multi-channel / format-0) track independently of the rest.
public sealed record PartOverrideDto(int TrackIndex, int Channel, string? PartName, string SourcePath, int SourceBank, int SourceProgram, double GainDb = 0);
// Per-part mixer strip, keyed by (track, channel) — the engine mixes by Synthesizer.TrackPart(track,
// channel). All fields default to no-ops. `inserts` is the part's Tier-2 insert rack.
public sealed record InstrumentMixDto(int TrackIndex, int Channel, double GainDb = 0, double Pan = 0, bool Mute = false, bool Solo = false, double ReverbSend = 0, double ChorusSend = 0, EffectDto[]? Inserts = null);
// A live per-part insert-rack update.
public sealed record InstrumentInsertDto(int TrackIndex, int Channel, EffectDto[]? Effects = null);
// One master-EQ band. type is "lowshelf" | "highshelf" | "peaking" (others map to peaking).
public sealed record EqBandDto(string Type, double FreqHz, double Q, double GainDb);
// One effect in a rack (ordered insert chain). type = "eq" | "limiter" | "plugin"; only the fields its
// type uses are read. enabled=false leaves it in the rack's order but out of the signal path. For
// "plugin": PluginFormat/PluginId pick the hosted plugin, InstanceId keys its live instance across
// reconfigures (so a param tweak reuses it instead of reloading), PluginParams are normalized 0..1
// values per parameter index, and PluginState is an optional base64 opaque-state blob.
public sealed record EffectDto(string Type, bool Enabled = true, EqBandDto[]? EqBands = null,
    double CeilingDb = -1.0, double ReleaseMs = 100.0,
    string? PluginFormat = null, string? PluginId = null, string? InstanceId = null,
    double[]? PluginParams = null, string? PluginState = null);
// Master-bus DSP: an ordered effect rack. The legacy scalar fields are still accepted (older clients /
// saved setups) and synthesized into an [eq, limiter] rack when `effects` is absent.
public sealed record MasterDto(EffectDto[]? Effects = null, double MasterGainDb = 0,
    bool LimiterEnabled = false, double CeilingDb = -1.0, double ReleaseMs = 100.0,
    bool EqEnabled = false, EqBandDto[]? EqBands = null);
// Binds a MIDI channel to a hosted plugin instrument: the channel's notes play through the plugin
// (summed into the mix) instead of the SoundFont synth, which is muted on that channel.
public sealed record InstrumentBindingDto(int Channel, string Format, string Id);
// Open/close a loaded plugin's editor by its insert InstanceId.
public sealed record EditorRequest(string InstanceId, string? Title = null);
public sealed record PlayRequest(string? DeviceId, string MidiPath, string SoundfontPath, PatchOverrideDto[]? Overrides = null, TrackOverrideDto[]? TrackOverrides = null, PartOverrideDto[]? PartOverrides = null, InstrumentMixDto[]? Mix = null, MasterDto? Master = null, InstrumentBindingDto[]? Instruments = null);
public sealed record PlayResponse(bool Ok, double DurationSeconds, string[] Defects, string? Error);
public sealed record StatusDto(string State, double PositionSeconds, double DurationSeconds, string? Midi, string? Soundfont);
public sealed record UsedPatchDto(int Bank, int Program, string? Name, bool IsDrum, int[] Channels);
public sealed record TrackInfoDto(int TrackIndex, string? Name, int[] Channels, int[] Programs, string? BaseName);
// One mixer strip = one song "part" = a (track, channel). Name/Sound are resolved server-side from the
// best info the file gives (track name when it uniquely identifies the part, else GM program name).
// CanSubstitute is always true now (per-part overrides route each (track, channel) independently); the
// field stays on the wire for older clients that gate the `src` button on it.
public sealed record PartDto(int TrackIndex, int Channel, string Name, string? Sound, bool IsDrum, bool CanSubstitute);
public sealed record SoundfontPatchDto(int Bank, int Program, string Name);
public sealed record SoundfontCatalogDto(string Name, SoundfontPatchDto[] Patches);

/// <summary>
/// Owns the live playback engine (synth + player + audio output) for the web server.
/// One piece plays at a time; <see cref="Play"/> tears down any previous playback first.
/// When a piece completes the server stays up and returns to "completed" (ready for the
/// next song); the process only exits on an explicit <see cref="RequestExit"/>.
/// </summary>
public sealed class PlayerService : IDisposable
{
    // Preferred rate: matches the Linux PipeWire/JACK graph rate (no resampling there). On Windows a
    // chosen device may open only at its own native rate (WASAPI/WDM-KS are exact-format), so the
    // effective rate is negotiated per play (see _sampleRate / SetSampleRate) and the whole pipeline
    // — synth, master rack, plugin host, inserts — is rebuilt at it. No resampling anywhere.
    private const int DefaultSampleRate = 48000;
    private const double TailSeconds = 2.0;   // render this long past the last event before "done"

    // The rate the current pipeline runs at. Starts at the preferred rate; SetSampleRate switches it
    // (and rebuilds the rate-dependent state) when a chosen device needs a different one.
    private int _sampleRate = DefaultSampleRate;

    private readonly IHostApplicationLifetime _lifetime;
    private readonly PluginHost _pluginHost;
    private readonly Lock _lock = new();

    public PlayerService(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
        _pluginHost = new PluginHost(DefaultSampleRate);
        _masterRack = new EffectRack(DefaultSampleRate, _pluginHost);
    }

    private OwnAudioOutput? _output;
    private RealtimePlayer? _player;
    private Synthesizer? _synth;
    private PatchMapSession? _session;   // owns the base + override source fonts for the current play

    // Master-bus DSP rack, caller-side (the synth has no reference to MidiSharp.Dsp). Persists across
    // plays (it's a setting); applied in the audio callback after the synth fills the block. Rebuilt
    // by SetSampleRate when the pipeline rate changes (its EQ/limiter are rate-dependent).
    private EffectRack _masterRack;
    // Per-track insert racks, keyed by trackIndex. Rebuilt from the play request and updated live;
    // a non-empty rack is registered with the synth as that track's IInstrumentInsert.
    private readonly Dictionary<int, EffectRack> _instrumentRacks = new();
    // Editor windows we host in-process (only when the sandbox is off; sandboxed plugins open their own in
    // the worker). Keyed by plugin InstanceId so a second open / a close can reach the same window.
    private readonly Dictionary<string, EditorWindow> _inProcEditors = new();
    // Hosted plugin instruments bound to channels for the current play; rendered and summed in the
    // audio callback. Torn down on stop. Each carries the part's mixer trim so gain/pan/mute/solo apply
    // to the summed plugin output the same way they do to synth voices.
    private readonly List<HostedVoice> _hostedInstruments = [];
    private float[] _instScratch = [];

    // A hosted instrument paired with the live mixer trim of the part it plays. The InstrumentMix is the
    // same mutable instance the synth holds for that part, so live fader moves (SetInstrumentMix) flow
    // through with no extra wiring. A part with no mixer strip gets a no-op identity mix → unity sum
    // (bit-identical to the pre-trim path). Insert is the part's effect rack (null = no insert); it is the
    // same instance the synth would carry, run here instead because the synth's copy of this channel is
    // muted. Mutable+volatile so a live SetInstrumentInsert add/remove reaches the audio thread.
    private sealed class HostedVoice(HostedInstrument inst, InstrumentMix mix, int part, EffectRack? insert)
    {
        private volatile EffectRack? _insert = insert;
        public HostedInstrument Inst { get; } = inst;
        public InstrumentMix Mix { get; } = mix;
        public int Part { get; } = part;
        public EffectRack? Insert { get => _insert; set => _insert = value; }
    }
    private PlayerState _state = PlayerState.Idle;
    private string? _midiName;
    private string? _soundfontName;
    private int _generation;   // bumped on every start/stop so stale completion monitors no-op

    // On PulseAudio/PipeWire, the meaningful output picker is the server's sinks (friendly names,
    // per-sink routable via move-sink-input). On bare ALSA / Windows / macOS there is no such
    // server, so fall back to OwnAudio's own device enumeration (selected via SetOutputDeviceByName).
    public IReadOnlyList<DeviceDto> GetDevices() =>
        PulseRouting.IsAvailable()
            ? PulseRouting.GetSinks()
            : OwnAudioOutput.GetOutputDevices()
                .Select(d => new DeviceDto(d.Id, d.Name, d.EngineName, d.IsDefault))
                .ToList();

    /// <summary>
    /// The patches a song actually uses, named against <paramref name="soundfontPath"/> — i.e.
    /// "what the song would normally play." Stateless: loads the base font transiently. Resolves
    /// banks identically to playback (via the analyzer's shared BankResolution).
    /// </summary>
    public IReadOnlyList<UsedPatchDto> GetSongPatches(string midiPath, string soundfontPath)
    {
        SmfRepairResult repair = SmfRepairFilter.Scan(File.ReadAllBytes(midiPath));
        MidiFile midi = MidiFileReader.Read(repair.Data);
        using IRBank bank = SoundBankLoader.Load(soundfontPath);
        return PatchUsageAnalyzer.Analyze(midi, bank)
            .Select(u => new UsedPatchDto(u.Bank, u.Program, u.BaseName, u.IsDrum, u.Channels.ToArray()))
            .ToList();
    }

    /// <summary>
    /// A song's tracks with the instrument each currently sounds (named against
    /// <paramref name="soundfontPath"/>) — the per-instrument view the user binds overrides to.
    /// Stateless: loads the base font transiently. Resolves banks identically to playback.
    /// </summary>
    public IReadOnlyList<TrackInfoDto> GetSongTracks(string midiPath, string soundfontPath)
    {
        SmfRepairResult repair = SmfRepairFilter.Scan(File.ReadAllBytes(midiPath));
        MidiFile midi = MidiFileReader.Read(repair.Data);
        using IRBank bank = SoundBankLoader.Load(soundfontPath);
        return TrackUsageAnalyzer.Analyze(midi, bank)
            .Select(t => new TrackInfoDto(t.TrackIndex, t.Name, t.Channels.ToArray(), t.Programs.ToArray(), t.BaseName))
            .ToList();
    }

    /// <summary>
    /// The song's mixer "parts" — one per (track, channel), the finest grouping the file separates the
    /// music into. Each is labeled from the best info available: the track name when it uniquely
    /// identifies the part (the track sounds on a single channel), otherwise the GM program name, else
    /// the channel number. So a normal multi-track file yields one part per track and a single-track
    /// (format 0) file yields one part per channel — every instrument surfaces either way.
    /// </summary>
    public IReadOnlyList<PartDto> GetSongParts(string midiPath, string soundfontPath)
    {
        SmfRepairResult repair = SmfRepairFilter.Scan(File.ReadAllBytes(midiPath));
        MidiFile midi = MidiFileReader.Read(repair.Data);
        using IRBank bank = SoundBankLoader.Load(soundfontPath);
        return PartUsageAnalyzer.Analyze(midi, bank).Select(p =>
        {
            bool nameFromTrack = p.TrackChannelCount == 1 && !string.IsNullOrWhiteSpace(p.TrackName);
            string name = nameFromTrack ? p.TrackName! : (p.BaseName ?? $"Ch {p.Channel + 1}");
            string progStr = p.IsDrum ? "kit" : "prog " + string.Join(",", p.Programs);
            // Sub-line: the program info, plus the GM name only when it isn't already the headline.
            string sound = p.BaseName != null && !string.Equals(p.BaseName, name, StringComparison.Ordinal)
                ? $"{progStr} · {p.BaseName}" : progStr;
            // Every part is independently substitutable now (per-part overrides route one (track,
            // channel) on its own), so the strip's `src` is always available.
            return new PartDto(p.TrackIndex, p.Channel, name, sound, p.IsDrum, CanSubstitute: true);
        }).ToList();
    }

    /// <summary>
    /// A source font's full instrument catalog (for the override picker), grouped/ordered by
    /// (bank, program). Stateless: loads the font transiently.
    /// </summary>
    public SoundfontCatalogDto GetSoundfontPatches(string path)
    {
        using IRBank bank = SoundBankLoader.Load(path);
        SoundfontPatchDto[] patches = bank.Patches
            .OrderBy(p => p.Bank).ThenBy(p => p.Program)
            .Select(p => new SoundfontPatchDto(p.Bank, p.Program, p.Name))
            .ToArray();
        return new SoundfontCatalogDto(bank.Name, patches);
    }

    public PlayResponse Play(PlayRequest req)
    {
        lock (_lock)
        {
            StopLocked();
            try
            {
                if (!File.Exists(req.MidiPath))
                    return new PlayResponse(false, 0, [], $"MIDI not found: {req.MidiPath}");
                if (!File.Exists(req.SoundfontPath))
                    return new PlayResponse(false, 0, [], $"SoundFont not found: {req.SoundfontPath}");

                // Repair → strict read → load base font + override sources → compose → wire up.
                SmfRepairResult repair = SmfRepairFilter.Scan(File.ReadAllBytes(req.MidiPath));
                MidiFile midi = MidiFileReader.Read(repair.Data);

                // Device routing splits by environment. On PulseAudio/PipeWire, req.DeviceId is a
                // server sink name: open OwnAudio on the system default (ALSA "default") and move our
                // own stream to the chosen sink after start. On bare ALSA / Windows / macOS, req.DeviceId
                // is an OwnAudio device id. Negotiate the rate FIRST (before building the synth), because
                // a chosen device may only open at its own native rate — then run the whole pipeline at
                // that rate (no resampling). Pulse and the system-default path keep the preferred rate.
                bool routeViaPulse = PulseRouting.IsAvailable();
                string? targetSink = string.IsNullOrEmpty(req.DeviceId) ? null : req.DeviceId;
                int rate = !routeViaPulse && targetSink is not null
                    ? OwnAudioOutput.NegotiateSampleRate(targetSink, DefaultSampleRate)
                    : DefaultSampleRate;
                SetSampleRate(rate);

                // The session owns the base font and every override source font for this play.
                // Assigned to _session immediately so StopLocked disposes it even if a later
                // step throws. The composite it builds is a borrowed view the synth consumes.
                var session = new PatchMapSession(SoundBankLoader.Load(req.SoundfontPath));
                _session = session;
                var sources = new Dictionary<string, IRBank>(StringComparer.OrdinalIgnoreCase);
                ApplyOverrides(session, req.Overrides, sources);
                ApplyTrackOverrides(session, req.TrackOverrides, sources);
                ApplyPartOverrides(session, req.PartOverrides, sources);

                var synth = new Synthesizer(_sampleRate);
                CompositeResult composite = session.BuildResolved();
                synth.LoadSoundFont(composite.Bank);
                synth.SetTrackPatchMap(composite.TrackPatchMap);
                synth.SetPartPatchMap(composite.PartPatchMap);
                // Override gainDb seeds the per-instrument gain; the mixer array then takes authority
                // (gain/pan/mute/solo/sends). Master DSP (limiter) is configured from the request too.
                ApplyInstrumentGains(synth, req.Overrides, req.TrackOverrides, req.PartOverrides, composite);
                ApplyInstrumentMix(synth, req.Mix);
                ApplyInstrumentInserts(synth, req.Mix);
                ConfigureMaster(req.Master);
                _masterRack.Reset();
                var player = new RealtimePlayer(midi, synth);
                ApplyInstrumentBindings(req.Instruments, req.Mix, synth, player);
                // Device + rate were resolved up front (see top of Play). Open at the negotiated rate.
                var output = new OwnAudioOutput(_sampleRate, channels: 2,
                    outputDeviceId: routeViaPulse ? null : targetSink);
                // Synth fills the block (per-instrument inserts already applied inside it); any hosted
                // plugin instruments render their bound channels and sum in; the master rack processes the
                // summed mix caller-side before output.
                output.SetCallback((buffer, frames) =>
                {
                    Span<float> span = buffer.AsSpan(0, frames * 2);
                    player.ProcessBlockInterleaved(span);
                    if (_hostedInstruments.Count > 0)
                    {
                        int nn = frames * 2;
                        if (_instScratch.Length < nn) _instScratch = new float[nn];
                        Span<float> sc = _instScratch.AsSpan(0, nn);
                        bool anySolo = synth.AnySolo;
                        foreach (HostedVoice hv in _hostedInstruments)
                        {
                            // Always render (keeps the plugin's clock and event queue advancing) even when
                            // gated to silence, so resuming a mute/solo doesn't skip the plugin's timeline.
                            hv.Inst.Render(sc);
                            InstrumentMix mix = hv.Mix;
                            // mute/solo gate the whole part; gain/pan trim the summed stereo output.
                            if (mix.Mute || (anySolo && !mix.Solo)) continue;
                            EffectRack? insert = hv.Insert;
                            if (insert == null)
                            {
                                StereoTrim.Add(span, sc, mix.GainDb, mix.Pan);   // no insert → trim straight into the mix
                            }
                            else
                            {
                                // Trim pre-insert (matching the synth's voice→bus→insert order), run the
                                // part's insert chain on the trimmed signal, then sum the wet result in.
                                StereoTrim.Apply(sc, mix.GainDb, mix.Pan);
                                insert.Process(sc);
                                for (var i = 0; i < nn; i++) span[i] += sc[i];
                            }
                        }
                    }
                    _masterRack.Process(span);
                });
                output.Start();

                // PulseAudio/PipeWire: route our own (uniquely-named) stream onto the requested sink.
                // Best-effort and off the lock-critical path — failure just leaves it on the default.
                if (routeViaPulse && targetSink is not null)
                    _ = Task.Run(() => PulseRouting.MoveOurStreamToSink(targetSink));

                _output = output;
                _player = player;
                _synth = synth;
                _state = PlayerState.Playing;
                _midiName = Path.GetFileName(req.MidiPath);
                _soundfontName = Path.GetFileName(req.SoundfontPath);

                int gen = ++_generation;
                _ = Task.Run(() => MonitorCompletionAsync(gen, player));

                return new PlayResponse(true, player.Duration.TotalSeconds,
                    repair.Defects.Select(d => d.ToString()).ToArray(), null);
            }
            catch (Exception ex)
            {
                StopLocked();
                return new PlayResponse(false, 0, [], ex.Message);
            }
        }
    }

    // Load each distinct override source font once (shared across patch- and track-overrides via
    // the byPath cache), register it with the session (which owns its lifetime), and map each
    // logical (bank, program) to the chosen source patch.
    private static void ApplyOverrides(PatchMapSession session, PatchOverrideDto[]? overrides,
        Dictionary<string, IRBank> byPath)
    {
        if (overrides == null || overrides.Length == 0) return;

        foreach (PatchOverrideDto o in overrides)
        {
            IRBank src = LoadSource(session, byPath, o.SourcePath);
            session.SetOverride(o.LogicalBank, o.LogicalProgram, new PatchRef(src, o.SourceBank, o.SourceProgram));
        }
    }

    // Force every note from a given track to a chosen source patch, sharing the byPath cache so a
    // font used by both a patch- and a track-override loads only once.
    private static void ApplyTrackOverrides(PatchMapSession session, TrackOverrideDto[]? overrides,
        Dictionary<string, IRBank> byPath)
    {
        if (overrides == null || overrides.Length == 0) return;

        foreach (TrackOverrideDto o in overrides)
        {
            IRBank src = LoadSource(session, byPath, o.SourcePath);
            session.SetTrackOverride(o.TrackIndex, new PatchRef(src, o.SourceBank, o.SourceProgram));
        }
    }

    // Force every note on a given (track, channel) part to a chosen source patch — the finest-grained
    // substitution, used where a single track carries several instruments (format 0). Shares the byPath
    // cache so a font reused across patch/track/part overrides loads only once.
    private static void ApplyPartOverrides(PatchMapSession session, PartOverrideDto[]? overrides,
        Dictionary<string, IRBank> byPath)
    {
        if (overrides == null || overrides.Length == 0) return;

        foreach (PartOverrideDto o in overrides)
        {
            IRBank src = LoadSource(session, byPath, o.SourcePath);
            session.SetPartOverride(o.TrackIndex, o.Channel, new PatchRef(src, o.SourceBank, o.SourceProgram));
        }
    }

    // Apply each override's per-instrument gain trim (dB) to the loaded synth. The trim is keyed by the
    // instrument id a note resolves to: a patch override's logical (bank, program), or a track
    // override's synthetic address from the composite's track→patch map. This is the orchestration
    // balance knob — e.g. lifting CC1-dynamics strings (authored ~30 dB down) that a GM file's CC1=0
    // would otherwise pin to silence. Zero gain is skipped so an all-zero setup leaves the engine on
    // its bit-identical pre-mixer path.
    private static void ApplyInstrumentGains(Synthesizer synth, PatchOverrideDto[]? overrides,
        TrackOverrideDto[]? trackOverrides, PartOverrideDto[]? partOverrides, CompositeResult composite)
    {
        if (overrides != null)
            foreach (PatchOverrideDto o in overrides)
                if (o.GainDb != 0)
                    synth.GetInstrumentMix(o.LogicalBank, o.LogicalProgram).GainDb = o.GainDb;

        if (trackOverrides != null)
            foreach (TrackOverrideDto o in trackOverrides)
                if (o.GainDb != 0 && composite.TrackPatchMap.TryGetValue(o.TrackIndex, out (int Bank, int Program) addr))
                    synth.GetInstrumentMix(addr.Bank, addr.Program).GainDb = o.GainDb;

        // A part override's gain seeds the part's mixer bucket — the (TrackMixBank, TrackPart) id the
        // voice is actually tagged with — so it lands on the same strip the user sees.
        if (partOverrides == null) return;
        foreach (PartOverrideDto o in partOverrides)
            if (o.GainDb != 0)
                synth.GetInstrumentMix(Synthesizer.TrackMixBank, Synthesizer.TrackPart(o.TrackIndex, o.Channel)).GainDb = o.GainDb;
    }

    // Bind hosted plugin instruments to channels: load each, mute its channel on the synth, and route
    // that channel's note/CC events (sample-accurately, via the player's EventDispatched hook) to the
    // plugin. The plugins are rendered and summed in the audio callback.
    private void ApplyInstrumentBindings(InstrumentBindingDto[]? bindings, InstrumentMixDto[]? mix,
        Synthesizer synth, RealtimePlayer player)
    {
        if (bindings == null) return;
        foreach (InstrumentBindingDto b in bindings)
        {
            HostedInstrument inst;
            try { inst = new HostedInstrument(_pluginHost.Load(b.Format, b.Id), _pluginHost.Config); }
            catch { continue; }   // missing/incompatible plugin → leave the channel on the synth

            int channel = b.Channel;
            // The bind is per-channel (the synth mutes the whole channel); the mixer is per-(track,channel)
            // part. Pair the instrument with the mix strip for its channel — the SAME InstrumentMix
            // instance the synth holds, so a live fader move updates both. No strip → a no-op identity
            // mix, leaving the summed output at unity (bit-identical to before per-part trim existed).
            InstrumentMixDto? strip = mix?.FirstOrDefault(m => m.Channel == channel);
            int part = strip != null ? Synthesizer.TrackPart(strip.TrackIndex, channel) : -1;
            InstrumentMix partMix = strip != null
                ? synth.GetInstrumentMix(Synthesizer.TrackMixBank, part)
                : new InstrumentMix();
            // The part's insert rack (built by ApplyInstrumentInserts, which ran before this) is run on the
            // plugin output in the callback — the synth's copy of this channel is muted, so its bus never
            // would. An empty/absent rack means no insert.
            EffectRack? insert = strip != null && _instrumentRacks.TryGetValue(part, out EffectRack? r) && !r.IsEmpty ? r : null;
            synth.MuteChannel(channel, true);
            player.EventDispatched += (se, offset) =>
            {
                switch (se.Event)
                {
                    case NoteOnEvent e when e.Channel == channel:
                        if (e.Velocity == 0) inst.NoteOff(offset, channel, e.Note);
                        else inst.NoteOn(offset, channel, e.Note, e.Velocity);
                        break;
                    case NoteOffEvent e when e.Channel == channel:
                        inst.NoteOff(offset, channel, e.Note);
                        break;
                    case ControlChangeEvent e when e.Channel == channel:
                        inst.QueueEvent(HostEvent.Midi(offset, (byte)(0xB0 | channel), (byte)e.Controller, (byte)e.Value));
                        break;
                }
            };
            _hostedInstruments.Add(new HostedVoice(inst, partMix, part, insert));
        }
    }

    // Apply the per-instrument mixer strips to the loaded synth. gainDb here is absolute and supersedes
    // the override-seeded gain; pan/mute/solo/sends are the rest of the Tier-1 strip.
    private static void ApplyInstrumentMix(Synthesizer synth, InstrumentMixDto[]? mix)
    {
        if (mix == null) return;
        foreach (InstrumentMixDto m in mix) ApplyOneMix(synth, m);
    }

    private static void ApplyOneMix(Synthesizer synth, InstrumentMixDto m)
    {
        InstrumentMix im = synth.GetInstrumentMix(Synthesizer.TrackMixBank, Synthesizer.TrackPart(m.TrackIndex, m.Channel));
        im.GainDb = m.GainDb;
        im.Pan = m.Pan;
        im.Mute = m.Mute;
        im.Solo = m.Solo;
        im.ReverbSend = m.ReverbSend;
        im.ChorusSend = m.ChorusSend;
    }

    // Switch the pipeline's sample rate, rebuilding everything rate-dependent. Called at the top of a
    // play (before the synth/racks are built) once the device's rate is negotiated. No-op when the rate
    // is unchanged. The plugin host keeps its discovered-plugin list (rate only affects the Config used
    // when instances are created); the master rack and the cached per-part insert racks are torn down
    // and rebuilt at the new rate (their EQ/limiter and plugin instances are rate-dependent). Safe here
    // because StopLocked has already quiesced the previous play's audio.
    private void SetSampleRate(int rate)
    {
        if (rate == _sampleRate) return;
        _sampleRate = rate;
        _pluginHost.SetSampleRate(rate);
        try { _masterRack.Dispose(); } catch { /* best-effort */ }
        _masterRack = new EffectRack(rate, _pluginHost);
        foreach (EffectRack r in _instrumentRacks.Values) { try { r.Dispose(); } catch { } }
        _instrumentRacks.Clear();
    }

    // Configure the master rack from an ordered effect list (the explicit `effects`, else an
    // [eq, limiter] rack synthesized from the legacy scalar fields for back-compat).
    private void ConfigureMaster(MasterDto? master) => _masterRack.Configure(ResolveEffects(master), master?.MasterGainDb ?? 0);

    private static EffectDto[] ResolveEffects(MasterDto? m)
    {
        if (m == null) return [];
        if (m.Effects != null) return m.Effects;
        return
        [
            new EffectDto("eq", m.EqEnabled, m.EqBands),
            new EffectDto("limiter", m.LimiterEnabled, null, m.CeilingDb, m.ReleaseMs)
        ];
    }

    /// <summary>
    /// Enrich a setup being saved with the live opaque state of each plugin insert (master bus and each
    /// per-part rack), so a stateful plugin's settings persist in the setup. A plugin that isn't currently
    /// loaded keeps whatever <c>PluginState</c> the setup already carried.
    /// </summary>
    public SetupDto CaptureStates(SetupDto setup)
    {
        lock (_lock)
        {
            MasterDto? master = setup.Master is { Effects: { } eff }
                ? setup.Master with { Effects = EnrichEffects(eff, _masterRack) }
                : setup.Master;
            InstrumentMixDto[]? mix = setup.Mix?.Select(m =>
            {
                if (m.Inserts == null) return m;
                int part = Synthesizer.TrackPart(m.TrackIndex, m.Channel);
                return _instrumentRacks.TryGetValue(part, out EffectRack? rack)
                    ? m with { Inserts = EnrichEffects(m.Inserts, rack) }
                    : m;
            }).ToArray();
            return setup with { Master = master, Mix = mix };
        }
    }

    private static EffectDto[] EnrichEffects(EffectDto[] effects, EffectRack rack) =>
        effects.Select(e => e.Type == "plugin" && !string.IsNullOrEmpty(e.InstanceId)
            ? e with { PluginState = rack.GetPluginState(e.InstanceId) ?? e.PluginState }
            : e).ToArray();

    // Rebuild the per-instrument insert racks from the play request and register the non-empty ones
    // with the synth (an instrument with no inserts pays nothing — the synth stays on its bypass path).
    private void ApplyInstrumentInserts(Synthesizer synth, InstrumentMixDto[]? mix)
    {
        // Reconcile rather than dispose-and-rebuild: a part's rack persists across plays (and across stop),
        // so a plugin loaded while stopped — with its editor open — keeps its live instance instead of being
        // torn down on Play. EffectRack.Configure already reuses plugin instances by InstanceId. Only racks
        // for parts no longer present are disposed.
        var keep = new HashSet<int>();
        foreach (InstrumentMixDto m in mix ?? [])
        {
            if (m.Inserts == null || m.Inserts.Length == 0) continue;
            int part = Synthesizer.TrackPart(m.TrackIndex, m.Channel);
            keep.Add(part);
            if (!_instrumentRacks.TryGetValue(part, out EffectRack? rack))
            {
                rack = new EffectRack(_sampleRate, _pluginHost);
                _instrumentRacks[part] = rack;
            }
            rack.Configure(m.Inserts);
            synth.SetInstrumentInsert(Synthesizer.TrackMixBank, part, rack.IsEmpty ? null : rack);
        }
        foreach (int part in _instrumentRacks.Keys.Where(p => !keep.Contains(p)).ToList())
        {
            _instrumentRacks[part].Dispose();
            _instrumentRacks.Remove(part);
        }
    }

    /// <summary>
    /// Live per-instrument insert-rack update — reconfigures (or creates) the instrument's rack and
    /// registers/unregisters it with the running synth without a restart. The rack instance persists,
    /// so its DSP state survives edits.
    /// </summary>
    public void SetInstrumentInsert(InstrumentInsertDto dto)
    {
        lock (_lock)
        {
            int part = Synthesizer.TrackPart(dto.TrackIndex, dto.Channel);
            if (!_instrumentRacks.TryGetValue(part, out EffectRack? rack))
            {
                rack = new EffectRack(_sampleRate, _pluginHost);
                _instrumentRacks[part] = rack;
            }
            rack.Configure(dto.Effects);
            _synth?.SetInstrumentInsert(Synthesizer.TrackMixBank, part, rack.IsEmpty ? null : rack);
            // A hosted instrument on this part runs its insert in the audio callback (its synth channel is
            // muted), so reach it here too — keeps live insert add/remove in parity with synth voices.
            foreach (HostedVoice hv in _hostedInstruments)
                if (hv.Part == part) hv.Insert = rack.IsEmpty ? null : rack;
        }
    }

    /// <summary>
    /// Open the native editor window for a loaded plugin insert (by InstanceId). A sandboxed plugin opens
    /// its editor in the worker process that holds it; an in-process plugin opens a window we own and track.
    /// Returns false when the plugin isn't loaded or has no editor.
    /// </summary>
    public bool OpenPluginEditor(string instanceId, string title)
    {
        lock (_lock)
        {
            IHostedPlugin? plugin = FindLoadedPlugin(instanceId);
            if (plugin is SandboxedPlugin sp) return sp.OpenEditor(title);
            if (plugin?.Gui is not { HasEditor: true } gui) return false;
            if (_inProcEditors.TryGetValue(instanceId, out EditorWindow? existing)) { existing.Close(); _inProcEditors.Remove(instanceId); }
            EditorWindow? win = EditorWindow.Open(gui, title);
            if (win == null) return false;
            _inProcEditors[instanceId] = win;
            return true;
        }
    }

    /// <summary>Close a plugin's editor window if open (sandboxed → in the worker; in-process → ours).</summary>
    public void ClosePluginEditor(string instanceId)
    {
        lock (_lock)
        {
            if (FindLoadedPlugin(instanceId) is SandboxedPlugin sp) sp.CloseEditor();
            if (_inProcEditors.Remove(instanceId, out EditorWindow? win)) win.Close();
        }
    }

    // Search the master rack and every per-instrument rack for a loaded plugin by InstanceId.
    private IHostedPlugin? FindLoadedPlugin(string instanceId)
    {
        IHostedPlugin? p = _masterRack.FindPlugin(instanceId);
        if (p != null) return p;
        foreach (EffectRack rack in _instrumentRacks.Values)
            if (rack.FindPlugin(instanceId) is { } hit) return hit;
        return null;
    }

    /// <summary>
    /// Live per-instrument mixer update — applied to the currently-playing synth without a restart.
    /// No-op (but harmless) when nothing is playing; the browser re-sends the full mixer state on Play.
    /// </summary>
    public void SetInstrumentMix(InstrumentMixDto m)
    {
        lock (_lock)
            if (_synth != null) ApplyOneMix(_synth, m);
    }

    /// <summary>Live master-bus (limiter) update — applied immediately; persists across plays.</summary>
    public void SetMaster(MasterDto m)
    {
        lock (_lock) ConfigureMaster(m);
    }

    private static IRBank LoadSource(PatchMapSession session, Dictionary<string, IRBank> byPath, string path)
    {
        if (byPath.TryGetValue(path, out IRBank? src)) return src;
        src = SoundBankLoader.Load(path);
        session.AddSource(src);
        byPath[path] = src;
        return src;
    }

    private async Task MonitorCompletionAsync(int gen, RealtimePlayer player)
    {
        // Done on a clean natural end (IsFinished — all events out, every voice silent),
        // OR once we've rendered the whole piece plus a tail window. The time backstop
        // covers files with stuck notes / infinite-release patches whose voices never go
        // silent, so IsFinished would otherwise hang the monitor forever.
        var completionFrame = (long)((player.Duration.TotalSeconds + TailSeconds) * _sampleRate);
        try
        {
            while (gen == Volatile.Read(ref _generation)
                   && !player.IsFinished
                   && player.CurrentFrame < completionFrame)
                await Task.Delay(200);
        }
        catch { return; }

        lock (_lock)
        {
            if (gen != _generation) return;   // a newer playback superseded us
            StopLocked();
            _state = PlayerState.Completed;   // ready for the next song; server stays up
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopLocked();
            _state = PlayerState.Idle;
        }
    }

    private void StopLocked()
    {
        _generation++;   // invalidate any running monitor
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        _output = null;
        _player = null;
        _synth = null;
        // Hosted instruments outlived the audio callback (now stopped); dispose their native plugins.
        foreach (HostedVoice hv in _hostedInstruments) { try { hv.Inst.Dispose(); } catch { } }
        _hostedInstruments.Clear();
        // Close any in-process editor windows before their plugins go away with the racks.
        foreach (EditorWindow w in _inProcEditors.Values) { try { w.Close(); } catch { } }
        _inProcEditors.Clear();
        // Dispose after the audio output is stopped: the composite the synth held borrowed
        // these fonts' samples, so they must outlive the audio callback.
        try { _session?.Dispose(); } catch { }
        _session = null;
    }

    public StatusDto Status()
    {
        lock (_lock)
        {
            return new StatusDto(
                _state.ToString().ToLowerInvariant(),
                _player?.Position.TotalSeconds ?? 0,
                _player?.Duration.TotalSeconds ?? 0,
                _midiName,
                _soundfontName);
        }
    }

    public void RequestExit() => _lifetime.StopApplication();

    // ── Plugin discovery (delegates to the host's cross-format registry) ──
    public IReadOnlyList<PluginDescriptorDto> GetPlugins() => _pluginHost.List();
    public PluginInfoDto? GetPluginInfo(string format, string id) => _pluginHost.GetInfo(format, id);
    public void RescanPlugins() => _pluginHost.Rescan();

    public void Dispose()
    {
        Stop();
        lock (_lock)
        {
            _masterRack.Dispose();
            foreach (EffectRack r in _instrumentRacks.Values) r.Dispose();
            _instrumentRacks.Clear();
        }
    }
}
