using System;

namespace MidiSharp.Audio.Internal;

/// <summary>
/// Big-endian (MSB-first) bit reader over a byte buffer — the bit order FLAC's
/// frames use. Reads up to 32 bits at a time, unary codes, and supports
/// byte-alignment between a frame's bit-packed body and its byte-aligned CRC.
/// </summary>
internal sealed class BitReader
{
    private readonly byte[] _data;
    private long _bitPos;

    public BitReader(byte[] data, long startByte)
    {
        _data = data;
        _bitPos = startByte * 8;
    }

    public long BytePosition => (_bitPos + 7) >> 3;
    public bool AtEnd => (_bitPos >> 3) >= _data.Length;

    /// <summary>Read <paramref name="n"/> bits (0..32), MSB first, as an unsigned value.</summary>
    public uint ReadBits(int n)
    {
        uint result = 0;
        while (n > 0)
        {
            int byteIndex = (int)(_bitPos >> 3);
            int bitOffset = (int)(_bitPos & 7);
            int bitsLeft = 8 - bitOffset;
            int take = Math.Min(n, bitsLeft);
            int shift = bitsLeft - take;
            uint mask = (uint)((1 << take) - 1);
            uint bits = (uint)((_data[byteIndex] >> shift) & mask);
            result = (result << take) | bits;
            _bitPos += take;
            n -= take;
        }
        return result;
    }

    /// <summary>Read <paramref name="n"/> bits and sign-extend from the top bit.</summary>
    public int ReadBitsSigned(int n)
    {
        if (n == 0) return 0;
        uint v = ReadBits(n);
        if (n < 32 && (v & (1u << (n - 1))) != 0)
            return (int)(v - (1u << n));
        return (int)v;
    }

    /// <summary>Count zero bits up to and consuming the terminating 1 bit (FLAC Rice quotient).</summary>
    public int ReadUnary()
    {
        int count = 0;
        while (true)
        {
            int byteIndex = (int)(_bitPos >> 3);
            int bitOffset = (int)(_bitPos & 7);
            // Scan the rest of the current byte for a set bit.
            int b = _data[byteIndex] << bitOffset & 0xFF;
            if (b == 0)
            {
                count += 8 - bitOffset;
                _bitPos += 8 - bitOffset;
                continue;
            }
            // Leading zeros within the remaining bits of this byte.
            int lead = 0;
            while ((b & 0x80) == 0) { b <<= 1; lead++; }
            count += lead;
            _bitPos += lead + 1;  // consume the zeros and the terminating 1
            return count;
        }
    }

    public void AlignToByte() => _bitPos = (_bitPos + 7) & ~7L;
}
