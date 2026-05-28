using System.Collections.Generic;
using MidiSharp.Model.Events;

namespace MidiSharp.Model;

/// <summary>
/// Represents a track chunk (MTrk) containing a sequence of MIDI events.
/// </summary>
public sealed class MidiTrack
{
    public MidiTrack(IReadOnlyList<MidiEvent> events, string? name = null)
    {
        Events = events;
        Name = name;
    }

    /// <summary>
    /// The track name from a TrackName meta event, if present.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The events in this track, in order.
    /// </summary>
    public IReadOnlyList<MidiEvent> Events { get; }
}
