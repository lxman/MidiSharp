using System;
using System.Collections.Generic;
using MidiSharp.Model;
using MidiSharp.Model.Events;

namespace MidiSharp.Sequencing;

/// <summary>
/// Processes a MIDI file into a time-ordered sequence of events.
/// Merges multiple tracks and converts tick times to real times.
/// </summary>
public sealed class MidiSequencer
{
    private readonly MidiFile _file;
    private readonly TempoMap _tempoMap;
    private readonly List<ScheduledEvent> _events;
    private readonly TimeSpan _duration;

    public MidiSequencer(MidiFile file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _tempoMap = TempoMap.BuildFrom(file);
        _events = BuildTimeline();
        _duration = CalculateDuration();
    }

    /// <summary>
    /// The source MIDI file.
    /// </summary>
    public MidiFile File => _file;

    /// <summary>
    /// The tempo map for this sequence.
    /// </summary>
    public TempoMap TempoMap => _tempoMap;

    /// <summary>
    /// All events in time order.
    /// </summary>
    public IReadOnlyList<ScheduledEvent> Events => _events;

    /// <summary>
    /// The total duration of the sequence.
    /// </summary>
    public TimeSpan Duration => _duration;

    /// <summary>
    /// Ticks per quarter note from the file header.
    /// </summary>
    public int TicksPerQuarterNote => _tempoMap.TicksPerQuarterNote;

    /// <summary>
    /// Converts a tick position to real time.
    /// </summary>
    public TimeSpan TickToTime(long tick) => _tempoMap.TickToTime(tick);

    /// <summary>
    /// Converts real time to a tick position.
    /// </summary>
    public long TimeToTick(TimeSpan time) => _tempoMap.TimeToTick(time);

    /// <summary>
    /// Gets events within a time range.
    /// </summary>
    /// <param name="start">Start time (inclusive).</param>
    /// <param name="end">End time (exclusive).</param>
    public IEnumerable<ScheduledEvent> GetEventsInRange(TimeSpan start, TimeSpan end)
    {
        foreach (var evt in _events)
        {
            if (evt.AbsoluteTime >= end) yield break;
            if (evt.AbsoluteTime >= start) yield return evt;
        }
    }

    /// <summary>
    /// Gets events starting from a specific time.
    /// </summary>
    public IEnumerable<ScheduledEvent> GetEventsFrom(TimeSpan start)
    {
        foreach (var evt in _events)
        {
            if (evt.AbsoluteTime >= start) yield return evt;
        }
    }

    /// <summary>
    /// Gets the event index closest to the specified time.
    /// </summary>
    public int GetEventIndexAtTime(TimeSpan time)
    {
        var targetTick = TimeToTick(time);

        // Binary search for the first event at or after the target tick
        var low = 0;
        var high = _events.Count - 1;
        var result = _events.Count; // Default: past end

        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (_events[mid].AbsoluteTicks >= targetTick)
            {
                result = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Gets only the playable events (excludes meta events except tempo).
    /// </summary>
    public IEnumerable<ScheduledEvent> GetPlayableEvents()
    {
        foreach (var evt in _events)
        {
            // Include channel events and sysex
            if (evt.Event is ChannelEvent || evt.Event is SysExEvent)
            {
                yield return evt;
            }
        }
    }

    /// <summary>
    /// Gets playable events starting from a specific time.
    /// </summary>
    public IEnumerable<ScheduledEvent> GetPlayableEventsFrom(TimeSpan start)
    {
        foreach (var evt in GetEventsFrom(start))
        {
            if (evt.Event is ChannelEvent || evt.Event is SysExEvent)
            {
                yield return evt;
            }
        }
    }

    private List<ScheduledEvent> BuildTimeline()
    {
        var allEvents = new List<ScheduledEvent>();
        long sequenceIndex = 0;

        for (var trackIndex = 0; trackIndex < _file.Tracks.Count; trackIndex++)
        {
            var track = _file.Tracks[trackIndex];

            foreach (var evt in track.Events)
            {
                var time = _tempoMap.TickToTime(evt.AbsoluteTicks);
                allEvents.Add(new ScheduledEvent(evt.AbsoluteTicks, time, evt, trackIndex, sequenceIndex++));
            }
        }

        // Sort by time (ScheduledEvent implements IComparable)
        allEvents.Sort();

        return allEvents;
    }

    private TimeSpan CalculateDuration()
    {
        if (_events.Count == 0) return TimeSpan.Zero;

        // A sequence's nominal length is its last event of any track — but exporters commonly pad
        // the conductor track's EndOfTrack far past the final note (e.g. a delta of 0x3FFFF),
        // which would report minutes of trailing silence as "duration". Anchor instead on the last
        // NOTE event: every tick after it is provably silent (only meta/structural events remain),
        // so trimming that span cannot clip a single sample of audible output. Note-off is included,
        // so a held final chord is preserved. Fall back to the nominal length for note-less files
        // (tempo maps, lyric/automation-only tracks), where there is nothing to anchor on.
        long maxTick = 0;
        long lastNoteTick = 0;
        var anyNote = false;
        foreach (var evt in _events)
        {
            if (evt.AbsoluteTicks > maxTick) maxTick = evt.AbsoluteTicks;
            if (evt.Event is NoteOnEvent or NoteOffEvent)
            {
                anyNote = true;
                if (evt.AbsoluteTicks > lastNoteTick) lastNoteTick = evt.AbsoluteTicks;
            }
        }

        return _tempoMap.TickToTime(anyNote ? lastNoteTick : maxTick);
    }
}
