using System;
using System.Collections.Generic;
using System.IO;
using MidiSharp.Model;
using MidiSharp.Model.Events;

namespace MidiSharp.IO;

/// <summary>
/// Reads Standard MIDI Files (SMF).
/// </summary>
public static class MidiFileReader
{
    private const uint MThd = 0x4D546864; // "MThd"
    private const uint MTrk = 0x4D54726B; // "MTrk"

    /// <summary>
    /// Reads a MIDI file from a file path.
    /// </summary>
    public static MidiFile Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Read(bytes);
    }

    /// <summary>
    /// Reads a MIDI file from a stream.
    /// </summary>
    public static MidiFile Read(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Read(ms.ToArray());
    }

    /// <summary>
    /// Reads a MIDI file from a byte array.
    /// </summary>
    public static MidiFile Read(byte[] data)
    {
        return Read(data.AsSpan());
    }

    /// <summary>
    /// Reads a MIDI file from a span of bytes.
    /// </summary>
    /// <remarks>
    /// This is a strict Standard MIDI File parser: it assumes well-formed input
    /// and throws on malformed structure (a missing <c>MTrk</c> marker, a data
    /// byte with no running status, an unsupported system message). Real-world
    /// files with newline-translation or dropped-byte damage should be passed
    /// through <see cref="SmfRepairFilter"/> first, which corrects the byte
    /// stream and reports exactly what it changed.
    /// </remarks>
    public static MidiFile Read(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);
        var header = ReadHeader(ref reader);
        var tracks = new List<MidiTrack>(header.TrackCount);

        for (var i = 0; i < header.TrackCount; i++)
        {
            tracks.Add(ReadTrack(ref reader));
        }

        return new MidiFile(header, tracks);
    }

    private static MidiHeader ReadHeader(ref SpanReader reader)
    {
        var chunkType = reader.ReadUInt32BigEndian();
        if (chunkType != MThd)
            throw new InvalidDataException($"Expected MThd chunk, got 0x{chunkType:X8}");

        var length = reader.ReadUInt32BigEndian();
        if (length < 6)
            throw new InvalidDataException($"Header chunk too short: {length}");

        var format = reader.ReadUInt16BigEndian();
        var trackCount = reader.ReadUInt16BigEndian();
        var division = reader.ReadInt16BigEndian();

        // Skip any extra header bytes (future compatibility)
        if (length > 6)
            reader.Skip((int)(length - 6));

        return new MidiHeader(
            (MidiFormat)format,
            trackCount,
            TimeDivision.FromRawValue(division));
    }

    private static MidiTrack ReadTrack(ref SpanReader reader)
    {
        var chunkType = reader.ReadUInt32BigEndian();
        if (chunkType != MTrk)
            throw new InvalidDataException($"Expected MTrk chunk, got 0x{chunkType:X8}");

        var length = reader.ReadUInt32BigEndian();
        var trackData = reader.ReadBytes((int)length);
        var trackReader = new SpanReader(trackData);

        var events = new List<MidiEvent>();
        byte runningStatus = 0;
        long absoluteTicks = 0;
        string? trackName = null;

        while (!trackReader.IsAtEnd)
        {
            var deltaTicks = trackReader.ReadVariableLengthQuantity();
            absoluteTicks += deltaTicks;

            var evt = ReadEvent(ref trackReader, ref runningStatus, deltaTicks);
            evt.AbsoluteTicks = absoluteTicks;
            events.Add(evt);

            // Extract track name if present
            if (evt is MetaEvent { Type: MetaEventType.TrackName } meta)
                trackName = meta.Text;

            // End of track
            if (evt is MetaEvent { Type: MetaEventType.EndOfTrack })
                break;
        }

        return new MidiTrack(events, trackName);
    }

    private static MidiEvent ReadEvent(ref SpanReader reader, ref byte runningStatus, int deltaTicks)
    {
        var status = reader.PeekByte();

        // If high bit is set, it's a status byte
        if ((status & 0x80) != 0)
        {
            reader.Skip(1);

            // Meta event
            if (status == 0xFF)
                return ReadMetaEvent(ref reader, deltaTicks);

            // SysEx
            if (status == 0xF0 || status == 0xF7)
                return ReadSysExEvent(ref reader, status, deltaTicks);

            // System common messages don't use running status
            if (status >= 0xF0)
            {
                runningStatus = 0;
                // Handle other system messages if needed
                throw new NotSupportedException($"System message 0x{status:X2} not supported");
            }

            // Channel message - update running status
            runningStatus = status;
        }
        else
        {
            // Running status - reuse previous status byte
            if (runningStatus == 0)
                throw new InvalidDataException("Data byte without prior status");
            status = runningStatus;
        }

        return ReadChannelEvent(ref reader, status, deltaTicks);
    }

    private static MidiEvent ReadChannelEvent(ref SpanReader reader, byte status, int deltaTicks)
    {
        var channel = (byte)(status & 0x0F);
        var messageType = (byte)(status & 0xF0);

        return messageType switch
        {
            0x80 => new NoteOffEvent
            {
                DeltaTicks = deltaTicks,
                Channel = channel,
                Note = reader.ReadByte(),
                Velocity = reader.ReadByte()
            },
            0x90 => new NoteOnEvent
            {
                DeltaTicks = deltaTicks,
                Channel = channel,
                Note = reader.ReadByte(),
                Velocity = reader.ReadByte()
            },
            0xA0 => new PolyPressureEvent
            {
                DeltaTicks = deltaTicks,
                Channel = channel,
                Note = reader.ReadByte(),
                Pressure = reader.ReadByte()
            },
            0xB0 => new ControlChangeEvent
            {
                DeltaTicks = deltaTicks,
                Channel = channel,
                Controller = reader.ReadByte(),
                Value = reader.ReadByte()
            },
            0xC0 => new ProgramChangeEvent
            {
                DeltaTicks = deltaTicks,
                Channel = channel,
                Program = reader.ReadByte()
            },
            0xD0 => new ChannelPressureEvent
            {
                DeltaTicks = deltaTicks,
                Channel = channel,
                Pressure = reader.ReadByte()
            },
            0xE0 => ReadPitchBend(ref reader, channel, deltaTicks),
            _ => throw new InvalidDataException($"Unknown channel message type: 0x{messageType:X2}")
        };
    }

    private static PitchBendEvent ReadPitchBend(ref SpanReader reader, byte channel, int deltaTicks)
    {
        var lsb = reader.ReadByte();
        var msb = reader.ReadByte();
        var raw = (msb << 7) | lsb;
        var value = (short)(raw - 8192);

        return new PitchBendEvent
        {
            DeltaTicks = deltaTicks,
            Channel = channel,
            Value = value
        };
    }

    private static MetaEvent ReadMetaEvent(ref SpanReader reader, int deltaTicks)
    {
        var type = reader.ReadByte();
        var length = reader.ReadVariableLengthQuantity();
        var data = reader.ReadBytes(length).ToArray();

        return new MetaEvent
        {
            DeltaTicks = deltaTicks,
            Type = (MetaEventType)type,
            Data = data
        };
    }

    private static SysExEvent ReadSysExEvent(ref SpanReader reader, byte status, int deltaTicks)
    {
        var length = reader.ReadVariableLengthQuantity();
        var data = reader.ReadBytes(length).ToArray();

        return new SysExEvent
        {
            DeltaTicks = deltaTicks,
            IsContinuation = status == 0xF7,
            Data = data
        };
    }
}
