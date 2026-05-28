namespace MidiSharp.Model.Events;

/// <summary>
/// Base class for all MIDI events.
/// </summary>
public abstract class MidiEvent
{
    /// <summary>
    /// Delta time in ticks relative to the previous event.
    /// </summary>
    public int DeltaTicks { get; init; }

    /// <summary>
    /// Absolute time in ticks from the start of the track.
    /// Calculated during file loading.
    /// </summary>
    public long AbsoluteTicks { get; set; }
}

/// <summary>
/// Base class for channel voice and mode messages (status 0x80-0xEF).
/// </summary>
public abstract class ChannelEvent : MidiEvent
{
    /// <summary>
    /// The MIDI channel (0-15).
    /// </summary>
    public byte Channel { get; init; }
}
