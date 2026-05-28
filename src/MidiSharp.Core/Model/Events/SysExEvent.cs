using System;

namespace MidiSharp.Model.Events;

/// <summary>
/// System Exclusive event (status 0xF0 or 0xF7).
/// </summary>
public sealed class SysExEvent : MidiEvent
{
    /// <summary>
    /// True if this is a continuation packet (0xF7), false if start (0xF0).
    /// </summary>
    public bool IsContinuation { get; init; }

    /// <summary>
    /// The SysEx data bytes (excluding status byte, including final 0xF7 if present).
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>
    /// The manufacturer ID (1 or 3 bytes at start of data).
    /// </summary>
    public ReadOnlyMemory<byte> ManufacturerId
    {
        get
        {
            if (Data.Length == 0) return ReadOnlyMemory<byte>.Empty;
            // If first byte is 0x00, it's a 3-byte ID
            if (Data.Span[0] == 0x00 && Data.Length >= 3)
                return Data[..3];
            return Data[..1];
        }
    }
}
