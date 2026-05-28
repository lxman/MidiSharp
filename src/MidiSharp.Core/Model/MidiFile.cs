using System.Collections.Generic;

namespace MidiSharp.Model;

/// <summary>
/// Represents a parsed Standard MIDI File (SMF).
/// </summary>
public sealed class MidiFile
{
    public MidiFile(MidiHeader header, IReadOnlyList<MidiTrack> tracks)
    {
        Header = header;
        Tracks = tracks;
    }

    /// <summary>
    /// The file header containing format, track count, and time division.
    /// </summary>
    public MidiHeader Header { get; }

    /// <summary>
    /// The tracks contained in this MIDI file.
    /// </summary>
    public IReadOnlyList<MidiTrack> Tracks { get; }
}
