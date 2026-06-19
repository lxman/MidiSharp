using System;
using MidiSharp.Model.Events;

namespace MidiSharp.Sequencing;

/// <summary>
/// An event scheduled at a specific time in the sequence.
/// </summary>
public readonly struct ScheduledEvent(
    long absoluteTicks,
    TimeSpan absoluteTime,
    MidiEvent midiEvent,
    int trackIndex,
    long sequenceIndex = 0)
    : IComparable<ScheduledEvent>
{
    /// <summary>
    /// The absolute tick position in the sequence.
    /// </summary>
    public long AbsoluteTicks { get; } = absoluteTicks;

    /// <summary>
    /// The absolute real-time position in the sequence.
    /// </summary>
    public TimeSpan AbsoluteTime { get; } = absoluteTime;

    /// <summary>
    /// The MIDI event.
    /// </summary>
    public MidiEvent Event { get; } = midiEvent;

    /// <summary>
    /// The index of the track this event came from.
    /// </summary>
    public int TrackIndex { get; } = trackIndex;

    /// <summary>
    /// The order in which this event was appended to the merged timeline (file order:
    /// track 0's events first, in their stored sequence, then track 1's, etc.). Used as the
    /// final, total-ordering tiebreaker so the sort is deterministic and stable even though
    /// <see cref="List{T}.Sort"/> is an unstable introsort.
    /// </summary>
    public long SequenceIndex { get; } = sequenceIndex;

    /// <summary>
    /// Compares events by absolute tick, then track index, then original append order.
    /// The <see cref="SequenceIndex"/> tiebreaker is essential: when several events share a
    /// tick (e.g. an RPN select CC101/CC100 immediately followed by its Data Entry CC6/CC38),
    /// an unstable sort would otherwise be free to reorder them — and processing the Data Entry
    /// before the RPN select silently drops the parameter write (pitch-bend range, etc.).
    /// </summary>
    public int CompareTo(ScheduledEvent other)
    {
        var tickCompare = AbsoluteTicks.CompareTo(other.AbsoluteTicks);
        if (tickCompare != 0) return tickCompare;

        var trackCompare = TrackIndex.CompareTo(other.TrackIndex);
        if (trackCompare != 0) return trackCompare;

        // Preserve original file order within a (tick, track) — keeps multi-byte
        // controller sequences (RPN/NRPN + Data Entry) in their authored order.
        return SequenceIndex.CompareTo(other.SequenceIndex);
    }

    public override string ToString() => $"{AbsoluteTime:mm\\:ss\\.fff} [{AbsoluteTicks}] Track {TrackIndex}: {Event.GetType().Name}";
}
