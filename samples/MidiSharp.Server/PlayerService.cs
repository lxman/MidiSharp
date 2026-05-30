using MidiSharp.IO;
using MidiSharp.SoundBank;
using MidiSharp.Synth;
using MidiSharp.Synth.OwnAudio;

namespace MidiSharp.Server;

public enum PlayerState { Idle, Playing, Completed }

public sealed record DeviceDto(string id, string name, string engine, bool isDefault);
public sealed record PlayRequest(string? deviceId, string midiPath, string soundfontPath);
public sealed record PlayResponse(bool ok, double durationSeconds, string[] defects, string? error);
public sealed record StatusDto(string state, double positionSeconds, double durationSeconds, string? midi, string? soundfont);

/// <summary>
/// Owns the live playback engine (synth + player + audio output) for the web server.
/// One piece plays at a time; <see cref="Play"/> tears down any previous playback first.
/// When a piece completes the server stays up and returns to "completed" (ready for the
/// next song); the process only exits on an explicit <see cref="RequestExit"/>.
/// </summary>
public sealed class PlayerService : IDisposable
{
    private const int SampleRate = 44100;
    private const double TailSeconds = 2.0;   // render this long past the last event before "done"

    private readonly IHostApplicationLifetime _lifetime;
    private readonly object _lock = new();

    private OwnAudioOutput? _output;
    private RealtimePlayer? _player;
    private Synthesizer? _synth;
    private PlayerState _state = PlayerState.Idle;
    private string? _midiName;
    private string? _soundfontName;
    private int _generation;   // bumped on every start/stop so stale completion monitors no-op

    public PlayerService(IHostApplicationLifetime lifetime) => _lifetime = lifetime;

    public IReadOnlyList<DeviceDto> GetDevices() =>
        OwnAudioOutput.GetOutputDevices()
            .Select(d => new DeviceDto(d.Id, d.Name, d.EngineName, d.IsDefault))
            .ToList();

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

                // Repair → strict read → load bank → wire synth + player + audio.
                var repair = SmfRepairFilter.Scan(File.ReadAllBytes(req.midiPath));
                var midi = MidiFileReader.Read(repair.Data);
                var bank = SoundBankLoader.Load(req.soundfontPath);

                var synth = new Synthesizer(SampleRate);
                synth.LoadSoundFont(bank);
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

    public void Dispose() => Stop();
}
