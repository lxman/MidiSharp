namespace MidiSharp.Model;

/// <summary>
/// Represents the header chunk (MThd) of a MIDI file.
/// </summary>
public sealed class MidiHeader
{
    public MidiHeader(MidiFormat format, int trackCount, TimeDivision division)
    {
        Format = format;
        TrackCount = trackCount;
        Division = division;
    }

    /// <summary>
    /// The MIDI file format (0, 1, or 2).
    /// </summary>
    public MidiFormat Format { get; }

    /// <summary>
    /// The number of track chunks in the file.
    /// </summary>
    public int TrackCount { get; }

    /// <summary>
    /// The time division specifying delta-time meaning.
    /// </summary>
    public TimeDivision Division { get; }
}

/// <summary>
/// MIDI file format types.
/// </summary>
public enum MidiFormat
{
    /// <summary>
    /// Single multi-channel track.
    /// </summary>
    SingleTrack = 0,

    /// <summary>
    /// One or more simultaneous tracks.
    /// </summary>
    MultiTrack = 1,

    /// <summary>
    /// One or more sequentially independent single-track patterns.
    /// </summary>
    MultiSequence = 2
}
