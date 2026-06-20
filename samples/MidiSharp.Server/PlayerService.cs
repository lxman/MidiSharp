using MidiSharp.IO;
using MidiSharp.Loader;
using MidiSharp.PatchMap;
using MidiSharp.Synth;
using MidiSharp.Synth.OwnAudio;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Server;

public enum PlayerState { Idle, Playing, Completed }

public sealed record DeviceDto(string Id, string Name, string Engine, bool IsDefault);
public sealed record PatchOverrideDto(int LogicalBank, int LogicalProgram, string SourcePath, int SourceBank, int SourceProgram, double GainDb = 0);
public sealed record TrackOverrideDto(int TrackIndex, string? TrackName, string SourcePath, int SourceBank, int SourceProgram, double GainDb = 0);
// Per-track mixer strip, keyed by source MIDI track (the engine mixes by track → (TrackMixBank,
// trackIndex)). All fields default to no-ops. `inserts` is the track's Tier-2 insert rack.
public sealed record InstrumentMixDto(int TrackIndex, double GainDb = 0, double Pan = 0, bool Mute = false, bool Solo = false, double ReverbSend = 0, double ChorusSend = 0, EffectDto[]? Inserts = null);
// A live per-track insert-rack update.
public sealed record InstrumentInsertDto(int TrackIndex, EffectDto[]? Effects = null);
// One master-EQ band. type is "lowshelf" | "highshelf" | "peaking" (others map to peaking).
public sealed record EqBandDto(string Type, double FreqHz, double Q, double GainDb);
// One effect in a rack (ordered insert chain). type = "eq" | "limiter"; only the fields its type uses
// are read. enabled=false leaves it in the rack's order but out of the signal path.
public sealed record EffectDto(string Type, bool Enabled = true, EqBandDto[]? EqBands = null,
    double CeilingDb = -1.0, double ReleaseMs = 100.0);
// Master-bus DSP: an ordered effect rack. The legacy scalar fields are still accepted (older clients /
// saved setups) and synthesized into an [eq, limiter] rack when `effects` is absent.
public sealed record MasterDto(EffectDto[]? Effects = null, double MasterGainDb = 0,
    bool LimiterEnabled = false, double CeilingDb = -1.0, double ReleaseMs = 100.0,
    bool EqEnabled = false, EqBandDto[]? EqBands = null);
public sealed record PlayRequest(string? DeviceId, string MidiPath, string SoundfontPath, PatchOverrideDto[]? Overrides = null, TrackOverrideDto[]? TrackOverrides = null, InstrumentMixDto[]? Mix = null, MasterDto? Master = null);
public sealed record PlayResponse(bool Ok, double DurationSeconds, string[] Defects, string? Error);
public sealed record StatusDto(string State, double PositionSeconds, double DurationSeconds, string? Midi, string? Soundfont);
public sealed record UsedPatchDto(int Bank, int Program, string? Name, bool IsDrum, int[] Channels);
public sealed record TrackInfoDto(int TrackIndex, string? Name, int[] Channels, int[] Programs, string? BaseName);
public sealed record SoundfontPatchDto(int Bank, int Program, string Name);
public sealed record SoundfontCatalogDto(string Name, SoundfontPatchDto[] Patches);

/// <summary>
/// Owns the live playback engine (synth + player + audio output) for the web server.
/// One piece plays at a time; <see cref="Play"/> tears down any previous playback first.
/// When a piece completes the server stays up and returns to "completed" (ready for the
/// next song); the process only exits on an explicit <see cref="RequestExit"/>.
/// </summary>
public sealed class PlayerService(IHostApplicationLifetime lifetime) : IDisposable
{
    private const int SampleRate = 48000;   // match PipeWire/JACK graph rate → no resampling
    private const double TailSeconds = 2.0;   // render this long past the last event before "done"

    private readonly object _lock = new();

    private OwnAudioOutput? _output;
    private RealtimePlayer? _player;
    private Synthesizer? _synth;
    private PatchMapSession? _session;   // owns the base + override source fonts for the current play

    // Master-bus DSP rack, caller-side (the synth has no reference to MidiSharp.Dsp). Persists across
    // plays (it's a setting); applied in the audio callback after the synth fills the block.
    private readonly EffectRack _masterRack = new(SampleRate);
    // Per-track insert racks, keyed by trackIndex. Rebuilt from the play request and updated live;
    // a non-empty rack is registered with the synth as that track's IInstrumentInsert.
    private readonly Dictionary<int, EffectRack> _instrumentRacks = new();
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

                var synth = new Synthesizer(SampleRate);
                var composite = session.BuildResolved();
                synth.LoadSoundFont(composite.Bank);
                synth.SetTrackPatchMap(composite.TrackPatchMap);
                // Override gainDb seeds the per-instrument gain; the mixer array then takes authority
                // (gain/pan/mute/solo/sends). Master DSP (limiter) is configured from the request too.
                ApplyInstrumentGains(synth, req.Overrides, req.TrackOverrides, composite);
                ApplyInstrumentMix(synth, req.Mix);
                ApplyInstrumentInserts(synth, req.Mix);
                ConfigureMaster(req.Master);
                _masterRack.Reset();
                var player = new RealtimePlayer(midi, synth);
                var output = new OwnAudioOutput(SampleRate, channels: 2,
                    outputDeviceId: string.IsNullOrEmpty(req.DeviceId) ? null : req.DeviceId);
                // Synth fills the block (per-instrument inserts already applied inside it); the master
                // rack processes the summed mix caller-side before output.
                output.SetCallback((buffer, frames) =>
                {
                    var span = buffer.AsSpan(0, frames * 2);
                    player.ProcessBlockInterleaved(span);
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

    // Apply each override's per-instrument gain trim (dB) to the loaded synth. The trim is keyed by the
    // instrument id a note resolves to: a patch override's logical (bank, program), or a track
    // override's synthetic address from the composite's track→patch map. This is the orchestration
    // balance knob — e.g. lifting CC1-dynamics strings (authored ~30 dB down) that a GM file's CC1=0
    // would otherwise pin to silence. Zero gain is skipped so an all-zero setup leaves the engine on
    // its bit-identical pre-mixer path.
    private static void ApplyInstrumentGains(Synthesizer synth, PatchOverrideDto[]? overrides,
        TrackOverrideDto[]? trackOverrides, CompositeResult composite)
    {
        if (overrides != null)
            foreach (var o in overrides)
                if (o.GainDb != 0)
                    synth.GetInstrumentMix(o.LogicalBank, o.LogicalProgram).GainDb = o.GainDb;

        if (trackOverrides != null)
            foreach (var o in trackOverrides)
                if (o.GainDb != 0 && composite.TrackPatchMap.TryGetValue(o.TrackIndex, out var addr))
                    synth.GetInstrumentMix(addr.Bank, addr.Program).GainDb = o.GainDb;
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
        var im = synth.GetInstrumentMix(Synthesizer.TrackMixBank, m.TrackIndex);
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

    // Rebuild the per-instrument insert racks from the play request and register the non-empty ones
    // with the synth (an instrument with no inserts pays nothing — the synth stays on its bypass path).
    private void ApplyInstrumentInserts(Synthesizer synth, InstrumentMixDto[]? mix)
    {
        _instrumentRacks.Clear();
        if (mix == null) return;
        foreach (var m in mix)
        {
            if (m.Inserts == null || m.Inserts.Length == 0) continue;
            var rack = new EffectRack(SampleRate);
            rack.Configure(m.Inserts);
            _instrumentRacks[m.TrackIndex] = rack;
            if (!rack.IsEmpty) synth.SetInstrumentInsert(Synthesizer.TrackMixBank, m.TrackIndex, rack);
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
            if (!_instrumentRacks.TryGetValue(dto.TrackIndex, out var rack))
            {
                rack = new EffectRack(SampleRate);
                _instrumentRacks[dto.TrackIndex] = rack;
            }
            rack.Configure(dto.Effects);
            _synth?.SetInstrumentInsert(Synthesizer.TrackMixBank, dto.TrackIndex, rack.IsEmpty ? null : rack);
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

    public void RequestExit() => lifetime.StopApplication();

    public void Dispose() => Stop();
}
