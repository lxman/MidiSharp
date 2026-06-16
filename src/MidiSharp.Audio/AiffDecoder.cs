using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace MidiSharp.Audio;

/// <summary>
/// AIFF and AIFF-C decoder. AIFF is a big-endian IFF container (FORM/COMM/SSND);
/// AIFF-C adds a compression tag in COMM — we handle the uncompressed variants
/// (<c>NONE</c>/<c>twos</c> big-endian, <c>sowt</c> little-endian, <c>fl32</c>/
/// <c>fl64</c> float). Loops come from INST (marker IDs) resolved against MARK.
/// </summary>
public sealed class AiffDecoder : IAudioDecoder
{
    public string Name => "AIFF";

    public bool CanDecode(ReadOnlySpan<byte> header, string? pathHint)
    {
        if (header.Length >= 12 &&
            header[0] == 'F' && header[1] == 'O' && header[2] == 'R' && header[3] == 'M' &&
            header[8] == 'A' && header[9] == 'I' && header[10] == 'F' &&
            (header[11] == 'F' || header[11] == 'C'))
            return true;
        return pathHint != null &&
               (pathHint.EndsWith(".aif", StringComparison.OrdinalIgnoreCase) ||
                pathHint.EndsWith(".aiff", StringComparison.OrdinalIgnoreCase) ||
                pathHint.EndsWith(".aifc", StringComparison.OrdinalIgnoreCase));
    }

    public AudioInfo Peek(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !Is(bytes, 0, 'F', 'O', 'R', 'M') ||
            !(Is(bytes, 8, 'A', 'I', 'F', 'F') || Is(bytes, 8, 'A', 'I', 'F', 'C')))
            return AudioInfo.None;

        int channels = 1, sampleRate = 44100, rootKey = -1; long frameCount = 0; double fine = 0;
        bool gotComm = false;

        int off = 12;
        while (off + 8 <= bytes.Length)
        {
            var id = bytes.Slice(off, 4);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(off + 4, 4));
            int body = off + 8;

            if (Is(id, 0, 'C', 'O', 'M', 'M') && body + 18 <= bytes.Length)
            {
                var c = bytes.Slice(body, 18);
                channels = BinaryPrimitives.ReadInt16BigEndian(c.Slice(0, 2));
                frameCount = BinaryPrimitives.ReadUInt32BigEndian(c.Slice(2, 4));
                sampleRate = (int)Math.Round(ReadExtended80(c.Slice(8, 10)));
                gotComm = true;
            }
            else if (Is(id, 0, 'I', 'N', 'S', 'T') && body + 2 <= bytes.Length)
            {
                rootKey = (sbyte)bytes[body];
                fine = (sbyte)bytes[body + 1];
            }

            long next = (long)body + size + (size & 1);
            if (next > bytes.Length) break;
            off = (int)next;
        }

        if (!gotComm) return AudioInfo.None;   // COMM not in the supplied bytes
        return new AudioInfo
        {
            Channels = channels < 1 ? 1 : channels,
            SampleRate = sampleRate <= 0 ? 44100 : sampleRate,
            FrameCount = frameCount,
            RootKey = rootKey,
            FineTuneCents = fine,
            LoopStartFrame = -1,   // AIFF loops are marker-based; SFZ opcodes drive loops here
            LoopEndFrame = -1,
        };
    }

    public DecodedAudio Decode(byte[] data)
    {
        var bytes = (ReadOnlySpan<byte>)data;
        if (bytes.Length < 12 || !Is(bytes, 0, 'F', 'O', 'R', 'M') ||
            !(Is(bytes, 8, 'A', 'I', 'F', 'F') || Is(bytes, 8, 'A', 'I', 'F', 'C')))
            throw new AudioDecodeException("Not an AIFF/AIFF-C file");

        int channels = 1, bits = 16;
        long frameCount = 0;
        int sampleRate = 44100;
        bool bigEndian = true, isFloat = false;
        ReadOnlySpan<byte> ssnd = default;
        int ssndOffset = 0;
        int rootKey = -1;
        double fineTuneCents = 0;
        int loopBeginMarker = 0, loopEndMarker = 0, loopPlayMode = 0;
        var markers = new Dictionary<int, long>();

        int off = 12;
        while (off + 8 <= bytes.Length)
        {
            var id = bytes.Slice(off, 4);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(off + 4, 4));
            int body = off + 8;
            long avail = bytes.Length - body;
            int chunkSize = (int)Math.Min(size, (uint)Math.Max(0, avail));
            var chunk = bytes.Slice(body, chunkSize);

            if (Is(id, 0, 'C', 'O', 'M', 'M') && chunkSize >= 18)
            {
                channels = BinaryPrimitives.ReadInt16BigEndian(chunk.Slice(0, 2));
                frameCount = BinaryPrimitives.ReadUInt32BigEndian(chunk.Slice(2, 4));
                bits = BinaryPrimitives.ReadInt16BigEndian(chunk.Slice(6, 2));
                sampleRate = (int)Math.Round(ReadExtended80(chunk.Slice(8, 10)));

                if (chunkSize >= 22)  // AIFF-C: 4-byte compression type follows
                {
                    var comp = chunk.Slice(18, 4);
                    if (Is(comp, 0, 's', 'o', 'w', 't')) bigEndian = false;
                    else if (Is(comp, 0, 'f', 'l', '3', '2') || Is(comp, 0, 'F', 'L', '3', '2')) { isFloat = true; bits = 32; }
                    else if (Is(comp, 0, 'f', 'l', '6', '4') || Is(comp, 0, 'F', 'L', '6', '4')) { isFloat = true; bits = 64; }
                    else if (Is(comp, 0, 'N', 'O', 'N', 'E') || Is(comp, 0, 't', 'w', 'o', 's')) { /* big-endian PCM */ }
                    else throw new AudioDecodeException(
                        $"Unsupported AIFF-C compression '{System.Text.Encoding.ASCII.GetString(comp.ToArray())}'");
                }
            }
            else if (Is(id, 0, 'S', 'S', 'N', 'D') && chunkSize >= 8)
            {
                ssndOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(chunk.Slice(0, 4));
                ssnd = chunk.Slice(8);  // skip offset + blockSize
            }
            else if (Is(id, 0, 'I', 'N', 'S', 'T') && chunkSize >= 20)
            {
                rootKey = (sbyte)chunk[0];
                fineTuneCents = (sbyte)chunk[1];
                loopPlayMode = BinaryPrimitives.ReadInt16BigEndian(chunk.Slice(8, 2));
                loopBeginMarker = BinaryPrimitives.ReadInt16BigEndian(chunk.Slice(10, 2));
                loopEndMarker = BinaryPrimitives.ReadInt16BigEndian(chunk.Slice(12, 2));
            }
            else if (Is(id, 0, 'M', 'A', 'R', 'K') && chunkSize >= 2)
            {
                int n = BinaryPrimitives.ReadUInt16BigEndian(chunk.Slice(0, 2));
                int p = 2;
                for (int i = 0; i < n && p + 6 <= chunk.Length; i++)
                {
                    int markerId = BinaryPrimitives.ReadUInt16BigEndian(chunk.Slice(p, 2));
                    long pos = BinaryPrimitives.ReadUInt32BigEndian(chunk.Slice(p + 2, 4));
                    markers[markerId] = pos;
                    int nameLen = chunk[p + 6];
                    int advance = 6 + 1 + nameLen;
                    if ((advance & 1) != 0) advance++;  // pstring padded to even
                    p += advance;
                }
            }

            off = body + chunkSize + (chunkSize & 1);
        }

        if (ssnd.IsEmpty || frameCount == 0)
            throw new AudioDecodeException("AIFF file has no sound data");
        if (ssndOffset > 0 && ssndOffset < ssnd.Length)
            ssnd = ssnd.Slice(ssndOffset);

        var (samples, frames) = DecodeSamples(ssnd, channels, bits, bigEndian, isFloat, frameCount);

        long loopStart = -1, loopEnd = -1;
        if (loopPlayMode != 0 &&
            markers.TryGetValue(loopBeginMarker, out long b2) &&
            markers.TryGetValue(loopEndMarker, out long e))
        {
            loopStart = b2;
            loopEnd = e;
            if (loopEnd > frames) loopEnd = frames;
            if (loopStart < 0 || loopStart >= loopEnd) { loopStart = -1; loopEnd = -1; }
        }

        return new DecodedAudio
        {
            Channels = channels < 1 ? 1 : channels,
            SampleRate = sampleRate <= 0 ? 44100 : sampleRate,
            BitsPerSample = bits,
            Samples = samples,
            FrameCount = frames,
            RootKey = rootKey is >= 0 and <= 127 ? rootKey : -1,
            FineTuneCents = fineTuneCents,
            LoopStartFrame = loopStart,
            LoopEndFrame = loopEnd,
        };
    }

    private static (float[] Samples, long Frames) DecodeSamples(
        ReadOnlySpan<byte> data, int channels, int bits, bool bigEndian, bool isFloat, long declaredFrames)
    {
        if (channels < 1) channels = 1;
        int bytesPerSample = (bits + 7) / 8;
        int bytesPerFrame = bytesPerSample * channels;
        if (bytesPerFrame <= 0) return (Array.Empty<float>(), 0);

        long frames = Math.Min(declaredFrames, data.Length / bytesPerFrame);
        int total = (int)(frames * channels);
        var output = new float[total];

        for (int i = 0; i < total; i++)
        {
            int p = i * bytesPerSample;
            if (isFloat && bits == 32)
            {
                int b = bigEndian ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(p, 4))
                                  : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(p, 4));
                output[i] = BitConverter.Int32BitsToSingle(b);
            }
            else if (isFloat && bits == 64)
            {
                long b = bigEndian ? BinaryPrimitives.ReadInt64BigEndian(data.Slice(p, 8))
                                   : BinaryPrimitives.ReadInt64LittleEndian(data.Slice(p, 8));
                output[i] = (float)BitConverter.Int64BitsToDouble(b);
            }
            else switch (bits)
            {
                case 8:
                    output[i] = (sbyte)data[p] / 128f;   // AIFF 8-bit is signed
                    break;
                case 16:
                {
                    short s = bigEndian ? BinaryPrimitives.ReadInt16BigEndian(data.Slice(p, 2))
                                        : BinaryPrimitives.ReadInt16LittleEndian(data.Slice(p, 2));
                    output[i] = s / 32768f;
                    break;
                }
                case 24:
                {
                    int v = bigEndian
                        ? ((sbyte)data[p] << 16) | (data[p + 1] << 8) | data[p + 2]
                        : ((sbyte)data[p + 2] << 16) | (data[p + 1] << 8) | data[p];
                    output[i] = v / 8388608f;
                    break;
                }
                case 32:
                {
                    int v = bigEndian ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(p, 4))
                                      : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(p, 4));
                    output[i] = v / 2147483648f;
                    break;
                }
                default:
                    return (Array.Empty<float>(), 0);
            }
        }

        return (output, frames);
    }

    /// <summary>Decode an 80-bit IEEE 754 extended-precision float (AIFF sample rate).</summary>
    private static double ReadExtended80(ReadOnlySpan<byte> b)
    {
        int sign = b[0] >> 7;
        int exponent = ((b[0] & 0x7F) << 8) | b[1];
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(2, 8));

        double value;
        if (exponent == 0 && mantissa == 0) value = 0;
        else if (exponent == 0x7FFF) value = double.NaN;
        else value = mantissa * Math.Pow(2.0, exponent - 16383 - 63);

        return sign != 0 ? -value : value;
    }

    private static bool Is(ReadOnlySpan<byte> s, int at, char a, char b, char c, char d) =>
        s.Length >= at + 4 && s[at] == a && s[at + 1] == b && s[at + 2] == c && s[at + 3] == d;
}
