namespace MidiSharp.Model.Events;

/// <summary>
/// Note On event (status 0x90-0x9F).
/// </summary>
public sealed class NoteOnEvent : ChannelEvent
{
    /// <summary>
    /// The note number (0-127).
    /// </summary>
    public byte Note { get; init; }

    /// <summary>
    /// The velocity (0-127). Velocity of 0 is equivalent to Note Off.
    /// </summary>
    public byte Velocity { get; init; }
}

/// <summary>
/// Note Off event (status 0x80-0x8F).
/// </summary>
public sealed class NoteOffEvent : ChannelEvent
{
    /// <summary>
    /// The note number (0-127).
    /// </summary>
    public byte Note { get; init; }

    /// <summary>
    /// The release velocity (0-127).
    /// </summary>
    public byte Velocity { get; init; }
}

/// <summary>
/// Control Change event (status 0xB0-0xBF).
/// </summary>
public sealed class ControlChangeEvent : ChannelEvent
{
    /// <summary>
    /// The controller number (0-119 for controllers, 120-127 for channel mode).
    /// </summary>
    public byte Controller { get; init; }

    /// <summary>
    /// The controller value (0-127).
    /// </summary>
    public byte Value { get; init; }
}

/// <summary>
/// Program Change event (status 0xC0-0xCF).
/// </summary>
public sealed class ProgramChangeEvent : ChannelEvent
{
    /// <summary>
    /// The program/patch number (0-127).
    /// </summary>
    public byte Program { get; init; }
}

/// <summary>
/// Pitch Bend event (status 0xE0-0xEF).
/// </summary>
public sealed class PitchBendEvent : ChannelEvent
{
    /// <summary>
    /// The pitch bend value (-8192 to +8191, center = 0).
    /// </summary>
    public short Value { get; init; }

    /// <summary>
    /// The raw 14-bit value (0-16383, center = 8192).
    /// </summary>
    public int RawValue => Value + 8192;
}

/// <summary>
/// Channel Pressure (Aftertouch) event (status 0xD0-0xDF).
/// </summary>
public sealed class ChannelPressureEvent : ChannelEvent
{
    /// <summary>
    /// The pressure value (0-127).
    /// </summary>
    public byte Pressure { get; init; }
}

/// <summary>
/// Polyphonic Key Pressure event (status 0xA0-0xAF).
/// </summary>
public sealed class PolyPressureEvent : ChannelEvent
{
    /// <summary>
    /// The note number (0-127).
    /// </summary>
    public byte Note { get; init; }

    /// <summary>
    /// The pressure value (0-127).
    /// </summary>
    public byte Pressure { get; init; }
}
