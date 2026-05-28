using System;
using System.Threading;
using System.Threading.Tasks;
using MidiSharp.Abstractions;
using MidiSharp.Model;
using MidiSharp.Model.Events;
using MidiSharp.Sequencing;

namespace MidiSharp.Playback;

/// <summary>
/// Plays MIDI files through a MIDI output device.
/// </summary>
public sealed class MidiPlayer : IDisposable
{
    private readonly IMidiOutput _output;
    private readonly MidiSequencer _sequencer;
    private readonly object _lock = new object();

    private PlaybackState _state = PlaybackState.Stopped;
    private CancellationTokenSource? _cts;
    private Task? _playbackTask;
    private int _currentEventIndex;
    private TimeSpan _pausedPosition;
    private bool _disposed;

    /// <summary>
    /// Creates a new MIDI player.
    /// </summary>
    /// <param name="file">The MIDI file to play.</param>
    /// <param name="output">The MIDI output device.</param>
    public MidiPlayer(MidiFile file, IMidiOutput output)
    {
        _sequencer = new MidiSequencer(file ?? throw new ArgumentNullException(nameof(file)));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>
    /// The current playback state.
    /// </summary>
    public PlaybackState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>
    /// The current playback position.
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                if (_state == PlaybackState.Paused)
                    return _pausedPosition;
                if (_state == PlaybackState.Stopped)
                    return TimeSpan.Zero;

                // Estimate current position based on event index
                if (_currentEventIndex < _sequencer.Events.Count)
                    return _sequencer.Events[_currentEventIndex].AbsoluteTime;
                return _sequencer.Duration;
            }
        }
    }

    /// <summary>
    /// The total duration of the MIDI file.
    /// </summary>
    public TimeSpan Duration => _sequencer.Duration;

    /// <summary>
    /// The underlying sequencer.
    /// </summary>
    public MidiSequencer Sequencer => _sequencer;

    /// <summary>
    /// Fired when a MIDI event is sent to the output.
    /// </summary>
    public event EventHandler<MidiEventArgs>? EventSent;

    /// <summary>
    /// Fired when playback completes or is stopped.
    /// </summary>
    public event EventHandler? PlaybackFinished;

    /// <summary>
    /// Starts or resumes playback.
    /// </summary>
    public void Play()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            if (_state == PlaybackState.Playing) return;

            if (!_output.IsOpen)
                _output.Open();

            _cts = new CancellationTokenSource();
            var startPosition = _state == PlaybackState.Paused ? _pausedPosition : TimeSpan.Zero;

            if (_state == PlaybackState.Stopped)
                _currentEventIndex = 0;

            _state = PlaybackState.Playing;
            _playbackTask = PlaybackLoopAsync(startPosition, _cts.Token);
        }
    }

    /// <summary>
    /// Pauses playback.
    /// </summary>
    public void Pause()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            if (_state != PlaybackState.Playing) return;

            _cts?.Cancel();
            _state = PlaybackState.Paused;

            // All notes off to prevent stuck notes
            _output.AllNotesOff();
        }
    }

    /// <summary>
    /// Stops playback and resets to the beginning.
    /// </summary>
    public void Stop()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            if (_state == PlaybackState.Stopped) return;

            _cts?.Cancel();
            _state = PlaybackState.Stopped;
            _currentEventIndex = 0;
            _pausedPosition = TimeSpan.Zero;

            // All notes off and reset
            _output.AllNotesOff();
            _output.Reset();
        }

        PlaybackFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Seeks to a specific position.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (position > Duration)
            position = Duration;

        lock (_lock)
        {
            var wasPlaying = _state == PlaybackState.Playing;

            if (wasPlaying)
            {
                _cts?.Cancel();
            }

            // Find the event index at the new position
            _currentEventIndex = _sequencer.GetEventIndexAtTime(position);
            _pausedPosition = position;

            // All notes off to prevent stuck notes
            _output.AllNotesOff();

            if (wasPlaying)
            {
                _cts = new CancellationTokenSource();
                _state = PlaybackState.Playing;
                _playbackTask = PlaybackLoopAsync(position, _cts.Token);
            }
            else if (_state == PlaybackState.Stopped)
            {
                _state = PlaybackState.Paused;
            }
        }
    }

    private async Task PlaybackLoopAsync(TimeSpan startPosition, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var events = _sequencer.Events;

        try
        {
            for (var i = _currentEventIndex; i < events.Count; i++)
            {
                if (ct.IsCancellationRequested)
                    break;

                var scheduled = events[i];
                var eventTime = scheduled.AbsoluteTime;

                // Calculate when this event should fire relative to when we started
                var targetTime = eventTime - startPosition;
                var elapsed = DateTime.UtcNow - startTime;
                var delay = targetTime - elapsed;

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        lock (_lock) _currentEventIndex = i;
                        _pausedPosition = eventTime;
                        return;
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    lock (_lock) _currentEventIndex = i;
                    _pausedPosition = eventTime;
                    return;
                }

                // Send the event
                SendEvent(scheduled.Event);

                lock (_lock) _currentEventIndex = i + 1;
            }

            // Playback completed
            lock (_lock)
            {
                _state = PlaybackState.Stopped;
                _currentEventIndex = 0;
                _pausedPosition = TimeSpan.Zero;
            }

            PlaybackFinished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // Log or handle exception
            lock (_lock)
            {
                _state = PlaybackState.Stopped;
            }
            throw;
        }
    }

    private void SendEvent(MidiEvent evt)
    {
        switch (evt)
        {
            case NoteOnEvent e:
                _output.SendShortMessage((byte)(0x90 | e.Channel), e.Note, e.Velocity);
                break;

            case NoteOffEvent e:
                _output.SendShortMessage((byte)(0x80 | e.Channel), e.Note, e.Velocity);
                break;

            case ControlChangeEvent e:
                _output.SendShortMessage((byte)(0xB0 | e.Channel), e.Controller, e.Value);
                break;

            case ProgramChangeEvent e:
                _output.SendShortMessage((byte)(0xC0 | e.Channel), e.Program);
                break;

            case PitchBendEvent e:
                var val = e.RawValue;
                _output.SendShortMessage((byte)(0xE0 | e.Channel), (byte)(val & 0x7F), (byte)((val >> 7) & 0x7F));
                break;

            case ChannelPressureEvent e:
                _output.SendShortMessage((byte)(0xD0 | e.Channel), e.Pressure);
                break;

            case PolyPressureEvent e:
                _output.SendShortMessage((byte)(0xA0 | e.Channel), e.Note, e.Pressure);
                break;

            case SysExEvent e:
                _output.SendSysEx(e.Data.Span);
                break;

            case MetaEvent:
                // Meta events are not sent to MIDI output
                // (tempo changes are handled by the sequencer)
                break;
        }

        EventSent?.Invoke(this, new MidiEventArgs(evt));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MidiPlayer));
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Stop before marking disposed
        try
        {
            Stop();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }

        _disposed = true;
        _cts?.Dispose();
    }
}

/// <summary>
/// Playback state.
/// </summary>
public enum PlaybackState
{
    Stopped,
    Playing,
    Paused
}

/// <summary>
/// Event args for MIDI events.
/// </summary>
public sealed class MidiEventArgs : EventArgs
{
    public MidiEventArgs(MidiEvent midiEvent)
    {
        Event = midiEvent;
    }

    public MidiEvent Event { get; }
}
