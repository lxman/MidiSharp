using System;

namespace MidiSharp.IO;

/// <summary>
/// A lightweight reader for parsing binary data from a ReadOnlySpan.
/// </summary>
internal ref struct SpanReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position = 0;

    public int Position => _position;
    public int Remaining => _data.Length - _position;
    public bool IsAtEnd => _position >= _data.Length;

    public byte ReadByte()
    {
        if (_position >= _data.Length)
            throw new InvalidOperationException("End of data");
        return _data[_position++];
    }

    public byte PeekByte()
    {
        if (_position >= _data.Length)
            throw new InvalidOperationException("End of data");
        return _data[_position];
    }

    public void Skip(int count)
    {
        _position += count;
        if (_position > _data.Length)
            throw new InvalidOperationException("Skip past end of data");
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (_position + count > _data.Length)
            throw new InvalidOperationException($"Cannot read {count} bytes, only {Remaining} remaining");
        var result = _data.Slice(_position, count);
        _position += count;
        return result;
    }

    public ushort ReadUInt16BigEndian()
    {
        if (_position + 2 > _data.Length)
            throw new InvalidOperationException("Not enough data for uint16");
        var result = (ushort)((_data[_position] << 8) | _data[_position + 1]);
        _position += 2;
        return result;
    }

    public short ReadInt16BigEndian()
    {
        return (short)ReadUInt16BigEndian();
    }

    public uint ReadUInt32BigEndian()
    {
        if (_position + 4 > _data.Length)
            throw new InvalidOperationException("Not enough data for uint32");
        var result = ((uint)_data[_position] << 24) |
                     ((uint)_data[_position + 1] << 16) |
                     ((uint)_data[_position + 2] << 8) |
                     _data[_position + 3];
        _position += 4;
        return result;
    }

    /// <summary>
    /// Reads a MIDI variable-length quantity (VLQ).
    /// </summary>
    public int ReadVariableLengthQuantity()
    {
        var result = 0;
        byte b;

        do
        {
            if (_position >= _data.Length)
                throw new InvalidOperationException("Unexpected end of data in VLQ");

            b = _data[_position++];
            result = (result << 7) | (b & 0x7F);
        }
        while ((b & 0x80) != 0);

        return result;
    }
}
