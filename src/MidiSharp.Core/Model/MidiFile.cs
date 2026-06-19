using System.Collections.Generic;

namespace MidiSharp.Model;

/// <summary>
/// Represents a parsed Standard MIDI File (SMF).
/// </summary>
public sealed class MidiFile(MidiHeader header, IReadOnlyList<MidiTrack> tracks)
{
    /// <summary>
    /// The file header containing format, track count, and time division.
    /// </summary>
    public MidiHeader Header { get; } = header;

    /// <summary>
    /// The tracks contained in this MIDI file.
    /// </summary>
    public IReadOnlyList<MidiTrack> Tracks { get; } = tracks;
}
