using System;
using System.Text;

namespace MidiSharp.Model.Events;

/// <summary>
/// Meta event (status 0xFF).
/// </summary>
public sealed class MetaEvent : MidiEvent
{
    /// <summary>
    /// The meta event type.
    /// </summary>
    public MetaEventType Type { get; init; }

    /// <summary>
    /// The raw data bytes for this meta event.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>
    /// For text-based meta events, returns the text content.
    /// </summary>
    public string? Text
    {
        get
        {
            if (Type is >= MetaEventType.Text and <= MetaEventType.CuePoint)
                return Encoding.ASCII.GetString(Data.Span);
            if (Type == MetaEventType.TrackName || Type == MetaEventType.InstrumentName)
                return Encoding.ASCII.GetString(Data.Span);
            return null;
        }
    }

    /// <summary>
    /// For SetTempo events, returns microseconds per quarter note.
    /// </summary>
    public int? Tempo
    {
        get
        {
            if (Type != MetaEventType.SetTempo || Data.Length < 3)
                return null;
            var span = Data.Span;
            return (span[0] << 16) | (span[1] << 8) | span[2];
        }
    }

    /// <summary>
    /// For SetTempo events, returns BPM (beats per minute).
    /// </summary>
    public double? Bpm => Tempo.HasValue ? 60_000_000.0 / Tempo.Value : null;
}

/// <summary>
/// Meta event types (0xFF nn).
/// </summary>
public enum MetaEventType : byte
{
    SequenceNumber = 0x00,
    Text = 0x01,
    Copyright = 0x02,
    TrackName = 0x03,
    InstrumentName = 0x04,
    Lyric = 0x05,
    Marker = 0x06,
    CuePoint = 0x07,
    ProgramName = 0x08,
    DeviceName = 0x09,
    ChannelPrefix = 0x20,
    MidiPort = 0x21,
    EndOfTrack = 0x2F,
    SetTempo = 0x51,
    SmpteOffset = 0x54,
    TimeSignature = 0x58,
    KeySignature = 0x59,
    SequencerSpecific = 0x7F
}
