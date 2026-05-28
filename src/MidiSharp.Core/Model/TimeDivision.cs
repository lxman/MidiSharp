using System;

namespace MidiSharp.Model;

/// <summary>
/// Represents the time division from the MIDI header.
/// Can be either ticks per quarter note (metrical) or SMPTE-based.
/// </summary>
public readonly struct TimeDivision
{
    private readonly short _rawValue;

    private TimeDivision(short rawValue)
    {
        _rawValue = rawValue;
    }

    /// <summary>
    /// Creates a metrical time division (ticks per quarter note).
    /// </summary>
    public static TimeDivision FromTicksPerQuarterNote(int ticks)
    {
        if (ticks < 1 || ticks > 0x7FFF)
            throw new ArgumentOutOfRangeException(nameof(ticks));
        return new TimeDivision((short)ticks);
    }

    /// <summary>
    /// Creates a SMPTE time division.
    /// </summary>
    /// <param name="framesPerSecond">Frames per second (24, 25, 29, or 30).</param>
    /// <param name="ticksPerFrame">Ticks per frame.</param>
    public static TimeDivision FromSmpte(int framesPerSecond, int ticksPerFrame)
    {
        // SMPTE format: bit 15 = 1, bits 14-8 = negative frames/sec, bits 7-0 = ticks/frame
        var negFps = -framesPerSecond;
        var raw = (short)((negFps << 8) | (ticksPerFrame & 0xFF));
        return new TimeDivision(raw);
    }

    /// <summary>
    /// Creates a time division from the raw 16-bit value in the MIDI header.
    /// </summary>
    public static TimeDivision FromRawValue(short rawValue) => new(rawValue);

    /// <summary>
    /// True if this is SMPTE-based timing; false if metrical (ticks per quarter note).
    /// </summary>
    public bool IsSmpte => (_rawValue & 0x8000) != 0;

    /// <summary>
    /// Ticks per quarter note. Only valid when IsSmpte is false.
    /// </summary>
    public int TicksPerQuarterNote => IsSmpte ? 0 : _rawValue;

    /// <summary>
    /// Frames per second. Only valid when IsSmpte is true.
    /// Returns 24, 25, 29 (drop-frame), or 30.
    /// </summary>
    public int FramesPerSecond => IsSmpte ? -(sbyte)((_rawValue >> 8) & 0xFF) : 0;

    /// <summary>
    /// Ticks per frame. Only valid when IsSmpte is true.
    /// </summary>
    public int TicksPerFrame => IsSmpte ? _rawValue & 0xFF : 0;

    /// <summary>
    /// The raw 16-bit value as stored in the MIDI header.
    /// </summary>
    public short RawValue => _rawValue;

    public override string ToString()
    {
        return IsSmpte
            ? $"SMPTE {FramesPerSecond} fps, {TicksPerFrame} ticks/frame"
            : $"{TicksPerQuarterNote} ticks/quarter";
    }
}
