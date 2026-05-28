using System;
using MidiSharp.Model.Events;

namespace MidiSharp.Sequencing;

/// <summary>
/// An event scheduled at a specific time in the sequence.
/// </summary>
public readonly struct ScheduledEvent : IComparable<ScheduledEvent>
{
    public ScheduledEvent(long absoluteTicks, TimeSpan absoluteTime, MidiEvent midiEvent, int trackIndex)
    {
        AbsoluteTicks = absoluteTicks;
        AbsoluteTime = absoluteTime;
        Event = midiEvent;
        TrackIndex = trackIndex;
    }

    /// <summary>
    /// The absolute tick position in the sequence.
    /// </summary>
    public long AbsoluteTicks { get; }

    /// <summary>
    /// The absolute real-time position in the sequence.
    /// </summary>
    public TimeSpan AbsoluteTime { get; }

    /// <summary>
    /// The MIDI event.
    /// </summary>
    public MidiEvent Event { get; }

    /// <summary>
    /// The index of the track this event came from.
    /// </summary>
    public int TrackIndex { get; }

    /// <summary>
    /// Compares events by absolute tick position, then by track index for stability.
    /// </summary>
    public int CompareTo(ScheduledEvent other)
    {
        var tickCompare = AbsoluteTicks.CompareTo(other.AbsoluteTicks);
        if (tickCompare != 0) return tickCompare;

        // Secondary sort by track index for stable ordering
        return TrackIndex.CompareTo(other.TrackIndex);
    }

    public override string ToString() => $"{AbsoluteTime:mm\\:ss\\.fff} [{AbsoluteTicks}] Track {TrackIndex}: {Event.GetType().Name}";
}
