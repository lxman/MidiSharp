using System.Collections.Generic;
using MidiSharp.Model.Events;

namespace MidiSharp.Model;

/// <summary>
/// Represents a track chunk (MTrk) containing a sequence of MIDI events.
/// </summary>
public sealed class MidiTrack(IReadOnlyList<MidiEvent> events, string? name = null)
{
    /// <summary>
    /// The track name from a TrackName meta event, if present.
    /// </summary>
    public string? Name { get; set; } = name;

    /// <summary>
    /// The events in this track, in order.
    /// </summary>
    public IReadOnlyList<MidiEvent> Events { get; } = events;
}
