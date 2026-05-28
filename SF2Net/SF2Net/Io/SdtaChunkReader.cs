using System.Runtime.InteropServices;

namespace SF2Net.Io;

/// <summary>
/// Owns the sample data byte arrays (16-bit <c>smpl</c> and optional 24-bit <c>sm24</c> LS bytes)
/// and provides random access to a sample region in either 16- or 24-bit form.
/// </summary>
internal sealed class SdtaChunkReader
{
    private readonly ReadOnlyMemory<byte> _data16;
    private readonly ReadOnlyMemory<byte> _data24;
    private short[]? _decodedFallback;

    /// <summary>Number of 16-bit sample frames in the <c>smpl</c> chunk.</summary>
    public int FrameCount => _data16.Length / 2;

    public bool Has24BitData => !_data24.IsEmpty;

    public SdtaChunkReader(ReadOnlyMemory<byte> sdtaList)
    {
        var span = sdtaList.Span;
        if (BinaryHelpers.ReadTag(span, 0) != "sdta")
            throw new SoundFontException(SoundFontValidationCode.FileBroken, "Expected sdta form type");

        var smpl = ReadOnlyMemory<byte>.Empty;
        var sm24 = ReadOnlyMemory<byte>.Empty;

        var pos = 4;
        while (pos + 8 <= span.Length)
        {
            var tag = BinaryHelpers.ReadTag(span, pos);
            var size = BinaryHelpers.ReadUInt32LE(span, pos + 4);
            var bodyStart = pos + 8;
            if (bodyStart + size > span.Length)
                throw new SoundFontException(SoundFontValidationCode.FileBroken);
            var body = sdtaList.Slice(bodyStart, (int)size);
            switch (tag)
            {
                case "smpl": smpl = body; break;
                case "sm24": sm24 = body; break;
            }
            pos = bodyStart + (int)size;
            if ((size & 1) != 0) pos++;
        }

        _data16 = smpl;

        // Per SF2 spec §6.2: sm24 length must be ceil(smpl_length_in_frames / 2). If not, ignore it.
        long expected24 = (_data16.Length / 2 + 1) / 2;
        _data24 = sm24.Length == expected24 ? sm24 : ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>
    /// Returns the raw little-endian 16-bit smpl bytes for a frame range. Returns empty memory
    /// if the requested range is out of bounds — matches Qt's <c>QByteArray::mid()</c> tolerance,
    /// which the C++ sflib relies on to treat broken samples as empty.
    /// </summary>
    public ReadOnlyMemory<byte> GetRawBytes(uint startFrame, uint endFrame)
    {
        if (endFrame < startFrame) return ReadOnlyMemory<byte>.Empty;
        var start = (long)startFrame * 2;
        var len = (long)(endFrame - startFrame) * 2;
        if (start < 0 || start >= _data16.Length || start + len > _data16.Length)
            return ReadOnlyMemory<byte>.Empty;
        return _data16.Slice((int)start, (int)len);
    }

    /// <summary>
    /// Returns the full <c>smpl</c> chunk as a span of 16-bit samples. Zero-copy on little-endian
    /// platforms via <see cref="MemoryMarshal.Cast{TFrom,TTo}(ReadOnlySpan{TFrom})"/>; falls back
    /// to a one-time byte-swap copy on big-endian. Any 24-bit extension (<c>sm24</c>) is ignored
    /// by this view — use <see cref="GetSamples"/> for 24-bit-aware access.
    /// </summary>
    public ReadOnlySpan<short> GetAllSamples16()
    {
        if (BitConverter.IsLittleEndian)
            return MemoryMarshal.Cast<byte, short>(_data16.Span);

        if (_decodedFallback is null)
        {
            var frames = FrameCount;
            var buf = new short[frames];
            var src = _data16.Span;
            for (var i = 0; i < frames; i++)
                buf[i] = BinaryHelpers.ReadInt16LE(src, i * 2);
            _decodedFallback = buf;
        }
        return _decodedFallback;
    }

    /// <summary>Returns sample values for a frame range. Promoted to 24-bit if sm24 is present. Returns an empty array for out-of-bounds requests.</summary>
    public int[] GetSamples(uint startFrame, uint endFrame)
    {
        if (endFrame < startFrame) return [];
        var byteStart = (long)startFrame * 2;
        var byteLen = (long)(endFrame - startFrame) * 2;
        if (byteStart < 0 || byteStart + byteLen > _data16.Length) return [];
        var frames = (int)(endFrame - startFrame);
        var result = new int[frames];
        var s16 = _data16.Span.Slice((int)byteStart, frames * 2);
        if (_data24.IsEmpty)
        {
            for (var i = 0; i < frames; i++)
                result[i] = BinaryHelpers.ReadInt16LE(s16, i * 2);
        }
        else
        {
            // sm24 length is bounded to (data16/2) above, so the slice is always safe here.
            var s24 = _data24.Span.Slice((int)startFrame, frames);
            for (var i = 0; i < frames; i++)
            {
                int lo = s24[i];
                int mid = s16[i * 2];
                // Sign-extend from 24-bit two's complement
                int hi = (sbyte)s16[i * 2 + 1];
                result[i] = (hi << 16) | (mid << 8) | lo;
            }
        }
        return result;
    }
}
