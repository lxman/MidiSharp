using System;
using System.Buffers.Binary;

namespace MidiSharp.Audio;

/// <summary>
/// WAV (RIFF/WAVE) container decoder. Parses <c>fmt </c>, <c>data</c>, and
/// <c>smpl</c> chunks; tolerates and skips any others. Honors the RIFF rule that
/// odd-length chunks pad to an even boundary. PCM conversion delegates to
/// <see cref="PcmDecoder"/>.
/// </summary>
public sealed class WavDecoder : IAudioDecoder
{
    public string Name => "WAV";

    public bool CanDecode(ReadOnlySpan<byte> header, string? pathHint)
    {
        if (header.Length >= 12 &&
            header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
            header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E')
            return true;
        return pathHint != null && pathHint.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
    }

    public AudioInfo Peek(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 ||
            bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' ||
            bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
            return AudioInfo.None;

        int channels = 1, sampleRate = 44100, bitsPerSample = 16;
        long dataSize = -1, loopStart = -1, loopEnd = -1;
        int rootKey = -1; double fineTuneCents = 0;

        int off = 12;
        while (off + 8 <= bytes.Length)
        {
            var id = bytes.Slice(off, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(off + 4, 4));
            int body = off + 8;

            if (Is(id, 'f', 'm', 't', ' ') && body + 16 <= bytes.Length)
            {
                var f = bytes.Slice(body, 16);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(14, 2));
            }
            else if (Is(id, 'd', 'a', 't', 'a'))
            {
                dataSize = size;   // the declared size — we never read the body
            }
            else if (Is(id, 's', 'm', 'p', 'l') && body + 60 <= bytes.Length)
            {
                var s = bytes.Slice(body, 60);
                rootKey = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20, 4));
                fineTuneCents = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(24, 4)) / 4294967296.0 * 100.0;
                if (BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(28, 4)) > 0)
                {
                    loopStart = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(44, 4));
                    loopEnd = (long)BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(48, 4)) + 1;
                }
            }

            long next = (long)body + size + (size & 1);
            if (next > bytes.Length) break;   // chunk body runs past the prefix — stop walking
            off = (int)next;
        }

        if (dataSize < 0) return AudioInfo.None;   // didn't find the data header in the supplied bytes
        int frameBytes = Math.Max(1, channels * ((bitsPerSample + 7) / 8));
        long frames = dataSize / frameBytes;
        if (loopEnd > frames) loopEnd = frames;
        if (loopStart >= frames) { loopStart = -1; loopEnd = -1; }

        return new AudioInfo
        {
            Channels = channels < 1 ? 1 : channels,
            SampleRate = sampleRate <= 0 ? 44100 : sampleRate,
            FrameCount = frames,
            RootKey = rootKey,
            FineTuneCents = fineTuneCents,
            LoopStartFrame = loopStart,
            LoopEndFrame = loopEnd,
        };
    }

    public DecodedAudio Decode(byte[] data)
    {
        var bytes = (ReadOnlySpan<byte>)data;
        if (bytes.Length < 12 ||
            bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' ||
            bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
            throw new AudioDecodeException("Not a RIFF/WAVE file");

        int channels = 1, sampleRate = 44100, bitsPerSample = 16;
        bool isFloat = false;
        ReadOnlySpan<byte> dataChunk = default;
        int rootKey = -1;
        double fineTuneCents = 0;
        long loopStart = -1, loopEnd = -1;

        int off = 12;
        while (off + 8 <= bytes.Length)
        {
            var id = bytes.Slice(off, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(off + 4, 4));
            int body = off + 8;
            long avail = bytes.Length - body;
            int chunkSize = (int)Math.Min(size, (uint)Math.Max(0, avail));

            if (Is(id, 'f', 'm', 't', ' ') && chunkSize >= 16)
            {
                var f = bytes.Slice(body, chunkSize);
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(14, 2));
                if (tag == 3) isFloat = true;
                else if (tag == 0xFFFE && chunkSize >= 40)
                {
                    ushort sub = BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(24, 2));
                    if (sub == 3) isFloat = true;
                }
            }
            else if (Is(id, 'd', 'a', 't', 'a'))
            {
                dataChunk = bytes.Slice(body, chunkSize);
            }
            else if (Is(id, 's', 'm', 'p', 'l') && chunkSize >= 36)
            {
                var s = bytes.Slice(body, chunkSize);
                rootKey = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20, 4));
                uint pitchFrac = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(24, 4));
                fineTuneCents = pitchFrac / 4294967296.0 * 100.0;
                uint numLoops = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(28, 4));
                if (numLoops > 0 && chunkSize >= 36 + 24)
                {
                    var loop = s.Slice(36, 24);
                    uint start = BinaryPrimitives.ReadUInt32LittleEndian(loop.Slice(8, 4));
                    uint end = BinaryPrimitives.ReadUInt32LittleEndian(loop.Slice(12, 4));
                    loopStart = start;
                    loopEnd = (long)end + 1;  // smpl loop end is inclusive → exclusive
                }
            }

            off = body + chunkSize + (chunkSize & 1);
        }

        if (dataChunk.IsEmpty)
            throw new AudioDecodeException("WAV file has no data chunk");

        var (samples, frames) = PcmDecoder.Decode(dataChunk, channels, bitsPerSample, isFloat);
        if (loopEnd > frames) loopEnd = frames;
        if (loopStart >= frames) { loopStart = -1; loopEnd = -1; }

        return new DecodedAudio
        {
            Channels = channels < 1 ? 1 : channels,
            SampleRate = sampleRate <= 0 ? 44100 : sampleRate,
            BitsPerSample = bitsPerSample,
            Samples = samples,
            FrameCount = frames,
            RootKey = rootKey,
            FineTuneCents = fineTuneCents,
            LoopStartFrame = loopStart,
            LoopEndFrame = loopEnd,
        };
    }

    private static bool Is(ReadOnlySpan<byte> id, char a, char b, char c, char d) =>
        id[0] == a && id[1] == b && id[2] == c && id[3] == d;
}
