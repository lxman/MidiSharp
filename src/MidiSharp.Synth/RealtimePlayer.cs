using System;
using System.Threading;
using MidiSharp.Model;
using MidiSharp.Model.Events;
using MidiSharp.Sequencing;

namespace MidiSharp.Synth;

/// <summary>
/// Audio-thread-driven MIDI playback: every call to <see cref="ProcessBlock"/>
/// advances the player by exactly that many frames, dispatches all MIDI events
/// whose absolute time falls in that block, and produces the corresponding
/// audio via the underlying <see cref="Synthesizer"/>.
///
/// <para>
/// The player slaves to whatever clock pulls audio from it — typically the audio
/// backend's callback, or a tight render loop when writing to a WAV file — rather
/// than scheduling events with <c>Task.Delay</c> on a wall-clock thread (which is
/// sloppy on Windows due to the 15.6 ms system timer tick). Same code, sample-accurate
/// timing on every platform.
/// </para>
///
/// <para>
/// <see cref="RealtimePlayer"/> does not depend on any audio backend. Anything
/// that can repeatedly ask "give me N frames of audio" can drive it.
/// </para>
/// </summary>
public sealed class RealtimePlayer
{
    private readonly Synthesizer _synth;
    private readonly MidiSequencer _sequencer;
    private readonly int _sampleRate;

    // Audio-thread-owned state: only ProcessBlock mutates these. Reader threads
    // (UI showing position, IsFinished check) see them via Interlocked / volatile.
    private long _currentFrame;
    private int _eventIndex;
    private int _peakActiveVoices;

    private volatile bool _paused;

    // Temp buffers for ProcessBlockInterleaved — grow on demand, retained between calls
    // to avoid per-callback allocation.
    private float[] _tempL;
    private float[] _tempR;

    /// <summary>The synthesizer being driven.</summary>
    public Synthesizer Synthesizer => _synth;

    /// <summary>The underlying sequencer (events list, tempo map, duration).</summary>
    public MidiSequencer Sequencer => _sequencer;

    /// <summary>Total piece duration.</summary>
    public TimeSpan Duration => _sequencer.Duration;

    /// <summary>Current playback position in audio frames since playback start.</summary>
    public long CurrentFrame => Interlocked.Read(ref _currentFrame);

    /// <summary>Current playback position as a wall-clock TimeSpan.</summary>
    public TimeSpan Position => TimeSpan.FromSeconds((double)CurrentFrame / _sampleRate);

    /// <summary>How many events have been dispatched so far (out of <see cref="MidiSequencer.Events"/>.Count).</summary>
    public int EventsDispatched => Volatile.Read(ref _eventIndex);

    /// <summary>Peak number of simultaneously active voices observed so far.</summary>
    public int PeakActiveVoices => Volatile.Read(ref _peakActiveVoices);

    /// <summary>
    /// True once all events have been dispatched AND every voice has finished
    /// sounding (release tails included). Safe to read from any thread.
    /// </summary>
    public bool IsFinished => EventsDispatched >= _sequencer.Events.Count
                              && _synth.ActiveVoiceCount == 0;

    /// <summary>Whether playback is currently paused.</summary>
    public bool IsPaused => _paused;

    /// <summary>
    /// Fires when a non-timing meta event is dispatched at its scheduled audio frame —
    /// lyrics, markers, copyright, track name, cue points, time/key signature. The
    /// callback is invoked on the audio thread, so handlers must be cheap (don't block,
    /// don't touch UI directly; marshal to a UI thread first). Tempo / EndOfTrack
    /// events are filtered out — they're already consumed by the sequencer's timing pass.
    /// </summary>
    public event Action<MetaEvent>? MetaEventDispatched;

    /// <summary>
    /// Fires for each dispatched event with its sample offset within the current block, just before the
    /// synth handles it — so a host can route a channel's notes to an alternative sound source (e.g. a
    /// hosted plugin instrument) sample-accurately. Invoked on the audio thread: handlers must be cheap,
    /// non-blocking, and tolerate exceptions being swallowed.
    /// </summary>
    public event Action<ScheduledEvent, int>? EventDispatched;

    /// <summary>
    /// Creates a player. The player does not own the synth — the caller is responsible
    /// for the synth's lifetime, loaded SoundFont, etc.
    /// </summary>
    /// <param name="file">The MIDI file to play.</param>
    /// <param name="synth">The synthesizer to drive.</param>
    /// <param name="initialTempBufferFrames">Pre-allocate the interleaved-output temp
    /// buffers at this size to avoid first-callback growth. Default 8192 is well above
    /// any typical audio backend buffer.</param>
    public RealtimePlayer(MidiFile file, Synthesizer synth, int initialTempBufferFrames = 8192)
    {
        _synth = synth ?? throw new ArgumentNullException(nameof(synth));
        _sequencer = new MidiSequencer(file ?? throw new ArgumentNullException(nameof(file)));
        _sampleRate = synth.SampleRate;
        _tempL = new float[initialTempBufferFrames];
        _tempR = new float[initialTempBufferFrames];
    }

    /// <summary>Pause playback. The next <see cref="ProcessBlock"/> will produce silence
    /// and not advance the clock. Sustaining voices keep their state but stop sounding.</summary>
    public void Pause() => _paused = true;

    /// <summary>Resume playback from the current position.</summary>
    public void Resume() => _paused = false;

    /// <summary>
    /// Reset to the start: silence all voices, rewind to frame 0, clear paused state.
    /// Safe to call from a non-audio thread, but if a ProcessBlock is running concurrently
    /// the rewind will only take effect after that block finishes.
    /// </summary>
    public void Reset()
    {
        _synth.AllSoundOff();
        Interlocked.Exchange(ref _currentFrame, 0);
        Volatile.Write(ref _eventIndex, 0);
        Volatile.Write(ref _peakActiveVoices, 0);
        _paused = false;
    }

    /// <summary>
    /// Render the next block of audio. Dispatches any MIDI events whose absolute time
    /// falls within this block before generating each sub-segment, so events land at
    /// their exact sample position regardless of block size.
    /// </summary>
    /// <param name="left">Left channel output buffer (overwritten).</param>
    /// <param name="right">Right channel output buffer (must be same length as left).</param>
    public void ProcessBlock(Span<float> left, Span<float> right)
    {
        var frames = left.Length;
        if (right.Length != frames)
            throw new ArgumentException("Left and right buffers must be the same length.");

        if (_paused)
        {
            left.Clear();
            right.Clear();
            return;
        }

        var events = _sequencer.Events;
        var startFrame = _currentFrame;
        var produced = 0;

        while (produced < frames)
        {
            var absFrame = startFrame + produced;
            var subFrames = frames - produced;

            // If the next event lies inside the remaining sub-block, shorten the sub-block
            // so the event fires on its exact frame boundary.
            if (_eventIndex < events.Count)
            {
                var eventFrame = (long)(events[_eventIndex].AbsoluteTime.TotalSeconds * _sampleRate);
                if (eventFrame <= absFrame)
                {
                    // produced is the event's exact offset within this block (the sub-block split above
                    // advanced the cursor to the event frame before this fires).
                    if (EventDispatched != null)
                        try { EventDispatched(events[_eventIndex], produced); } catch { }
                    DispatchEvent(events[_eventIndex]);
                    _eventIndex++;
                    continue;
                }
                var framesUntilEvent = (int)(eventFrame - absFrame);
                if (framesUntilEvent < subFrames) subFrames = framesUntilEvent;
            }

            if (subFrames > 0)
            {
                _synth.Generate(left.Slice(produced, subFrames), right.Slice(produced, subFrames));
                produced += subFrames;

                var active = _synth.ActiveVoiceCount;
                if (active > _peakActiveVoices) _peakActiveVoices = active;
            }
        }

        Interlocked.Exchange(ref _currentFrame, startFrame + frames);
    }

    /// <summary>
    /// Convenience overload for audio backends that work in interleaved stereo
    /// (e.g. OwnAudioSharp, NAudio, WASAPI). The span length must equal <c>2 * frameCount</c>.
    /// </summary>
    public void ProcessBlockInterleaved(Span<float> interleavedStereo)
    {
        var frames = interleavedStereo.Length / 2;
        if (frames == 0) return;

        if (_tempL.Length < frames)
        {
            _tempL = new float[frames];
            _tempR = new float[frames];
        }
        var L = _tempL.AsSpan(0, frames);
        var R = _tempR.AsSpan(0, frames);

        ProcessBlock(L, R);

        for (var i = 0; i < frames; i++)
        {
            interleavedStereo[2 * i] = L[i];
            interleavedStereo[2 * i + 1] = R[i];
        }
    }

    private void DispatchEvent(in ScheduledEvent se)
    {
        var evt = se.Event;
        switch (evt)
        {
            case NoteOnEvent e:
                if (e.Velocity == 0) _synth.NoteOff(e.Channel, e.Note);
                else _synth.NoteOn(e.Channel, e.Note, e.Velocity, se.TrackIndex);
                break;
            case NoteOffEvent e:
                _synth.NoteOff(e.Channel, e.Note);
                break;
            case ControlChangeEvent e:
                _synth.ControlChange(e.Channel, e.Controller, e.Value);
                break;
            case ProgramChangeEvent e:
                _synth.ProgramChange(e.Channel, e.Program);
                break;
            case PitchBendEvent e:
                _synth.PitchBend(e.Channel, e.RawValue);
                break;
            case ChannelPressureEvent e:
                _synth.ChannelPressure(e.Channel, e.Pressure);
                break;
            case PolyPressureEvent e:
                _synth.PolyPressure(e.Channel, e.Note, e.Pressure);
                break;
            case SysExEvent e:
                _synth.SysEx(e.Data.Span);
                break;
            case MetaEvent m:
                // SetTempo + EndOfTrack are already consumed by the sequencer's timing pass.
                // ChannelPrefix / MidiPort intentionally dropped (single-synth single-port).
                // Lyrics, markers, signatures, text, etc. all surface to callers.
                if (m.Type != MetaEventType.SetTempo &&
                    m.Type != MetaEventType.EndOfTrack &&
                    m.Type != MetaEventType.ChannelPrefix &&
                    m.Type != MetaEventType.MidiPort)
                {
                    // Subscriber exceptions must not crash the audio thread — swallow
                    // and continue. A broken lyric-display handler shouldn't silence
                    // a playing song.
                    try { MetaEventDispatched?.Invoke(m); } catch { }
                }
                break;
        }
    }
}
