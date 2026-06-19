using MidiSharp.Loader;
using MidiSharp.IO;
using MidiSharp.PatchMap;
using MidiSharp.SoundBank;
using MidiSharp.Synth;
using MidiSharp.Synth.OwnAudio;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Server;

public enum PlayerState { Idle, Playing, Completed }

public sealed record DeviceDto(string id, string name, string engine, bool isDefault);
public sealed record PatchOverrideDto(int logicalBank, int logicalProgram, string sourcePath, int sourceBank, int sourceProgram);
public sealed record TrackOverrideDto(int trackIndex, string? trackName, string sourcePath, int sourceBank, int sourceProgram);
public sealed record PlayRequest(string? deviceId, string midiPath, string soundfontPath, PatchOverrideDto[]? overrides = null, TrackOverrideDto[]? trackOverrides = null);
public sealed record PlayResponse(bool ok, double durationSeconds, string[] defects, string? error);
public sealed record StatusDto(string state, double positionSeconds, double durationSeconds, string? midi, string? soundfont);
public sealed record UsedPatchDto(int bank, int program, string? name, bool isDrum, int[] channels);
public sealed record TrackInfoDto(int trackIndex, string? name, int[] channels, int[] programs, string? baseName);
public sealed record SoundfontPatchDto(int bank, int program, string name);
public sealed record SoundfontCatalogDto(string name, SoundfontPatchDto[] patches);

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
                if (!File.Exists(req.midiPath))
                    return new PlayResponse(false, 0, [], $"MIDI not found: {req.midiPath}");
                if (!File.Exists(req.soundfontPath))
                    return new PlayResponse(false, 0, [], $"SoundFont not found: {req.soundfontPath}");

                // Repair → strict read → load base font + override sources → compose → wire up.
                var repair = SmfRepairFilter.Scan(File.ReadAllBytes(req.midiPath));
                var midi = MidiFileReader.Read(repair.Data);

                // The session owns the base font and every override source font for this play.
                // Assigned to _session immediately so StopLocked disposes it even if a later
                // step throws. The composite it builds is a borrowed view the synth consumes.
                var session = new PatchMapSession(SoundBankLoader.Load(req.soundfontPath));
                _session = session;
                var sources = new Dictionary<string, IRBank>(StringComparer.OrdinalIgnoreCase);
                ApplyOverrides(session, req.overrides, sources);
                ApplyTrackOverrides(session, req.trackOverrides, sources);

                var synth = new Synthesizer(SampleRate);
                var composite = session.BuildResolved();
                synth.LoadSoundFont(composite.Bank);
                synth.SetTrackPatchMap(composite.TrackPatchMap);
                var player = new RealtimePlayer(midi, synth);
                var output = new OwnAudioOutput(SampleRate, channels: 2,
                    outputDeviceId: string.IsNullOrEmpty(req.deviceId) ? null : req.deviceId);
                output.SetCallback((buffer, frames) => player.ProcessBlockInterleaved(buffer.AsSpan(0, frames * 2)));
                output.Start();

                _output = output;
                _player = player;
                _synth = synth;
                _state = PlayerState.Playing;
                _midiName = Path.GetFileName(req.midiPath);
                _soundfontName = Path.GetFileName(req.soundfontPath);

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
            var src = LoadSource(session, byPath, o.sourcePath);
            session.SetOverride(o.logicalBank, o.logicalProgram, new PatchRef(src, o.sourceBank, o.sourceProgram));
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
            var src = LoadSource(session, byPath, o.sourcePath);
            session.SetTrackOverride(o.trackIndex, new PatchRef(src, o.sourceBank, o.sourceProgram));
        }
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
