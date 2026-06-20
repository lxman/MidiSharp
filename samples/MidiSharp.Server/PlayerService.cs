using MidiSharp.Hosting;
using MidiSharp.IO;
using MidiSharp.Loader;
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
    private const int SampleRate = 48000;   // match PipeWire/JACK graph rate → no resampling
    private const double TailSeconds = 2.0;   // render this long past the last event before "done"

    private readonly IHostApplicationLifetime _lifetime;
    private readonly PluginHost _pluginHost;
    private readonly object _lock = new();

    public PlayerService(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
        _pluginHost = new PluginHost(SampleRate);
        _masterRack = new EffectRack(SampleRate, _pluginHost);
    }

    private OwnAudioOutput? _output;
    private RealtimePlayer? _player;
    private Synthesizer? _synth;
    private PatchMapSession? _session;   // owns the base + override source fonts for the current play

    // Master-bus DSP rack, caller-side (the synth has no reference to MidiSharp.Dsp). Persists across
    // plays (it's a setting); applied in the audio callback after the synth fills the block.
    private readonly EffectRack _masterRack;
    // Per-track insert racks, keyed by trackIndex. Rebuilt from the play request and updated live;
    // a non-empty rack is registered with the synth as that track's IInstrumentInsert.
    private readonly Dictionary<int, EffectRack> _instrumentRacks = new();
    // Hosted plugin instruments bound to channels for the current play; rendered and summed in the
    // audio callback. Torn down on stop.
    private readonly List<HostedInstrument> _hostedInstruments = [];
    private float[] _instScratch = [];
    private PlayerState _state = PlayerState.Idle;
    private string? _midiName;
    private string? _soundfontName;
    private int _generation;   // bumped on every start/stop so stale completion monitors no-op

    public IReadOnlyList<DeviceDto> GetDevices() =>
        OwnAudioOutput.GetOutputDevices()
            .Select(d => new DeviceDto(d.Id, d.Name, d.EngineName, d.IsDefault))
            .ToList();

    /// <summary>
    /// The patches a song actually uses, named against <paramref name="soundfontPath"/> — i.e.
    /// "what the song would normally play." Stateless: loads the base font transiently. Resolves
    /// banks identically to playback (via the analyzer's shared BankResolution).
    /// </summary>
    public IReadOnlyList<UsedPatchDto> GetSongPatches(string midiPath, string soundfontPath)
    {
        var repair = SmfRepairFilter.Scan(File.ReadAllBytes(midiPath));
        var midi = MidiFileReader.Read(repair.Data);
        using var bank = SoundBankLoader.Load(soundfontPath);
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
        var repair = SmfRepairFilter.Scan(File.ReadAllBytes(midiPath));
        var midi = MidiFileReader.Read(repair.Data);
        using var bank = SoundBankLoader.Load(soundfontPath);
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
        var repair = SmfRepairFilter.Scan(File.ReadAllBytes(midiPath));
        var midi = MidiFileReader.Read(repair.Data);
        using var bank = SoundBankLoader.Load(soundfontPath);
        return PartUsageAnalyzer.Analyze(midi, bank).Select(p =>
        {
            var nameFromTrack = p.TrackChannelCount == 1 && !string.IsNullOrWhiteSpace(p.TrackName);
            var name = nameFromTrack ? p.TrackName! : (p.BaseName ?? $"Ch {p.Channel + 1}");
            var progStr = p.IsDrum ? "kit" : "prog " + string.Join(",", p.Programs);
            // Sub-line: the program info, plus the GM name only when it isn't already the headline.
            var sound = p.BaseName != null && !string.Equals(p.BaseName, name, StringComparison.Ordinal)
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
        using var bank = SoundBankLoader.Load(path);
        var patches = bank.Patches
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
                var repair = SmfRepairFilter.Scan(File.ReadAllBytes(req.MidiPath));
                var midi = MidiFileReader.Read(repair.Data);

                // The session owns the base font and every override source font for this play.
                // Assigned to _session immediately so StopLocked disposes it even if a later
                // step throws. The composite it builds is a borrowed view the synth consumes.
                var session = new PatchMapSession(SoundBankLoader.Load(req.SoundfontPath));
                _session = session;
                var sources = new Dictionary<string, IRBank>(StringComparer.OrdinalIgnoreCase);
                ApplyOverrides(session, req.Overrides, sources);
                ApplyTrackOverrides(session, req.TrackOverrides, sources);
                ApplyPartOverrides(session, req.PartOverrides, sources);

                var synth = new Synthesizer(SampleRate);
                var composite = session.BuildResolved();
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
                ApplyInstrumentBindings(req.Instruments, synth, player);
                var output = new OwnAudioOutput(SampleRate, channels: 2,
                    outputDeviceId: string.IsNullOrEmpty(req.DeviceId) ? null : req.DeviceId);
                // Synth fills the block (per-instrument inserts already applied inside it); any hosted
                // plugin instruments render their bound channels and sum in; the master rack processes the
                // summed mix caller-side before output.
                output.SetCallback((buffer, frames) =>
                {
                    var span = buffer.AsSpan(0, frames * 2);
                    player.ProcessBlockInterleaved(span);
                    if (_hostedInstruments.Count > 0)
                    {
                        var nn = frames * 2;
                        if (_instScratch.Length < nn) _instScratch = new float[nn];
                        var sc = _instScratch.AsSpan(0, nn);
                        foreach (var hi in _hostedInstruments)
                        {
                            hi.Render(sc);
                            for (var i = 0; i < nn; i++) span[i] += sc[i];
                        }
                    }
                    _masterRack.Process(span);
                });
                output.Start();

                _output = output;
                _player = player;
                _synth = synth;
                _state = PlayerState.Playing;
                _midiName = Path.GetFileName(req.MidiPath);
                _soundfontName = Path.GetFileName(req.SoundfontPath);

                var gen = ++_generation;
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

        foreach (var o in overrides)
        {
            var src = LoadSource(session, byPath, o.SourcePath);
            session.SetOverride(o.LogicalBank, o.LogicalProgram, new PatchRef(src, o.SourceBank, o.SourceProgram));
        }
    }

    // Force every note from a given track to a chosen source patch, sharing the byPath cache so a
    // font used by both a patch- and a track-override loads only once.
    private static void ApplyTrackOverrides(PatchMapSession session, TrackOverrideDto[]? overrides,
        Dictionary<string, IRBank> byPath)
    {
        if (overrides == null || overrides.Length == 0) return;

        foreach (var o in overrides)
        {
            var src = LoadSource(session, byPath, o.SourcePath);
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

        foreach (var o in overrides)
        {
            var src = LoadSource(session, byPath, o.SourcePath);
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
            foreach (var o in overrides)
                if (o.GainDb != 0)
                    synth.GetInstrumentMix(o.LogicalBank, o.LogicalProgram).GainDb = o.GainDb;

        if (trackOverrides != null)
            foreach (var o in trackOverrides)
                if (o.GainDb != 0 && composite.TrackPatchMap.TryGetValue(o.TrackIndex, out var addr))
                    synth.GetInstrumentMix(addr.Bank, addr.Program).GainDb = o.GainDb;

        // A part override's gain seeds the part's mixer bucket — the (TrackMixBank, TrackPart) id the
        // voice is actually tagged with — so it lands on the same strip the user sees.
        if (partOverrides != null)
            foreach (var o in partOverrides)
                if (o.GainDb != 0)
                    synth.GetInstrumentMix(Synthesizer.TrackMixBank, Synthesizer.TrackPart(o.TrackIndex, o.Channel)).GainDb = o.GainDb;
    }

    // Bind hosted plugin instruments to channels: load each, mute its channel on the synth, and route
    // that channel's note/CC events (sample-accurately, via the player's EventDispatched hook) to the
    // plugin. The plugins are rendered and summed in the audio callback.
    private void ApplyInstrumentBindings(InstrumentBindingDto[]? bindings, Synthesizer synth, RealtimePlayer player)
    {
        if (bindings == null) return;
        foreach (var b in bindings)
        {
            HostedInstrument inst;
            try { inst = new HostedInstrument(_pluginHost.Load(b.Format, b.Id), _pluginHost.Config); }
            catch { continue; }   // missing/incompatible plugin → leave the channel on the synth

            var channel = b.Channel;
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
            _hostedInstruments.Add(inst);
        }
    }

    // Apply the per-instrument mixer strips to the loaded synth. gainDb here is absolute and supersedes
    // the override-seeded gain; pan/mute/solo/sends are the rest of the Tier-1 strip.
    private static void ApplyInstrumentMix(Synthesizer synth, InstrumentMixDto[]? mix)
    {
        if (mix == null) return;
        foreach (var m in mix) ApplyOneMix(synth, m);
    }

    private static void ApplyOneMix(Synthesizer synth, InstrumentMixDto m)
    {
        var im = synth.GetInstrumentMix(Synthesizer.TrackMixBank, Synthesizer.TrackPart(m.TrackIndex, m.Channel));
        im.GainDb = m.GainDb;
        im.Pan = m.Pan;
        im.Mute = m.Mute;
        im.Solo = m.Solo;
        im.ReverbSend = m.ReverbSend;
        im.ChorusSend = m.ChorusSend;
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
            var master = setup.Master is { Effects: { } eff }
                ? setup.Master with { Effects = EnrichEffects(eff, _masterRack) }
                : setup.Master;
            var mix = setup.Mix?.Select(m =>
            {
                if (m.Inserts == null) return m;
                var part = Synthesizer.TrackPart(m.TrackIndex, m.Channel);
                return _instrumentRacks.TryGetValue(part, out var rack)
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
        foreach (var r in _instrumentRacks.Values) r.Dispose();   // free any previous play's plugin instances
        _instrumentRacks.Clear();
        if (mix == null) return;
        foreach (var m in mix)
        {
            if (m.Inserts == null || m.Inserts.Length == 0) continue;
            var rack = new EffectRack(SampleRate, _pluginHost);
            rack.Configure(m.Inserts);
            var part = Synthesizer.TrackPart(m.TrackIndex, m.Channel);
            _instrumentRacks[part] = rack;
            if (!rack.IsEmpty) synth.SetInstrumentInsert(Synthesizer.TrackMixBank, part, rack);
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
            var part = Synthesizer.TrackPart(dto.TrackIndex, dto.Channel);
            if (!_instrumentRacks.TryGetValue(part, out var rack))
            {
                rack = new EffectRack(SampleRate, _pluginHost);
                _instrumentRacks[part] = rack;
            }
            rack.Configure(dto.Effects);
            _synth?.SetInstrumentInsert(Synthesizer.TrackMixBank, part, rack.IsEmpty ? null : rack);
        }
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
        if (byPath.TryGetValue(path, out var src)) return src;
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
        var completionFrame = (long)((player.Duration.TotalSeconds + TailSeconds) * SampleRate);
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
        foreach (var hi in _hostedInstruments) { try { hi.Dispose(); } catch { } }
        _hostedInstruments.Clear();
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
            foreach (var r in _instrumentRacks.Values) r.Dispose();
            _instrumentRacks.Clear();
        }
    }
}
