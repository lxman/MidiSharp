using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MidiSharp.Audio;

/// <summary>
/// Decodes raw little-endian PCM bytes into normalized float32. The shared
/// primitive beneath WAV, AIFF (after byte-swap), and DLS embedded WAVE —
/// anything that hands over a contiguous block of interleaved integer/float
/// samples whose format is already known.
/// </summary>
public static class PcmDecoder
{
    /// <summary>
    /// Decode <paramref name="bytes"/> (interleaved by channel) into float32.
    /// Returns the samples and the resulting frame count (frames = floats / channels).
    /// </summary>
    public static (float[] Samples, long FrameCount) Decode(
        ReadOnlySpan<byte> bytes, int channels, int bitsPerSample, bool isFloat)
    {
        if (channels < 1) channels = 1;
        int bytesPerSample = (bitsPerSample + 7) / 8;
        int bytesPerFrame = bytesPerSample * channels;
        if (bytesPerFrame <= 0 || bytes.Length < bytesPerFrame)
            return (Array.Empty<float>(), 0);

        long frames = bytes.Length / bytesPerFrame;
        int totalSamples = (int)(frames * channels);
        var output = new float[totalSamples];

        if (isFloat && bitsPerSample == 32)
        {
            // BinaryPrimitives.ReadSingleLittleEndian doesn't exist on netstandard2.1.
            for (int i = 0; i < totalSamples; i++)
            {
                int bits = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4, 4));
                output[i] = BitConverter.Int32BitsToSingle(bits);
            }
        }
        else if (isFloat && bitsPerSample == 64)
        {
            for (int i = 0; i < totalSamples; i++)
            {
                long bits = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(i * 8, 8));
                output[i] = (float)BitConverter.Int64BitsToDouble(bits);
            }
        }
        else switch (bitsPerSample)
        {
            case 8:
            {
                // WAV 8-bit is unsigned, biased to 128.
                const float Scale = 1.0f / 128.0f;
                for (int i = 0; i < totalSamples; i++)
                    output[i] = (bytes[i] - 128) * Scale;
                break;
            }
            case 16:
            {
                const float Scale = 1.0f / 32768.0f;
                if (BitConverter.IsLittleEndian)
                    SampleConvert.Int16ToFloat(MemoryMarshal.Cast<byte, short>(bytes.Slice(0, totalSamples * 2)), output, Scale);
                else
                    for (int i = 0; i < totalSamples; i++)
                        output[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2)) * Scale;
                break;
            }
            case 24:
            {
                const float Scale = 1.0f / 8388608.0f;
                for (int i = 0; i < totalSamples; i++)
                {
                    int b0 = bytes[i * 3];
                    int b1 = bytes[i * 3 + 1];
                    int b2 = (sbyte)bytes[i * 3 + 2]; // sign-extend high byte
                    int v = (b2 << 16) | (b1 << 8) | b0;
                    output[i] = v * Scale;
                }
                break;
            }
            case 32:
            {
                const float Scale = 1.0f / 2147483648.0f;
                for (int i = 0; i < totalSamples; i++)
                    output[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4, 4)) * Scale;
                break;
            }
            default:
            {
                // Generic signed little-endian integer PCM for any 1..8-byte container — covers exotic
                // depths (40/48/56/64-bit) and sub-byte depths (12/20) stored MSB-justified in their byte
                // container. Normalised by the container's full range, so a byte-aligned depth is exact;
                // a sub-byte depth left-justified in the container reads correctly too. >64-bit is bogus.
                if (bytesPerSample is < 1 or > 8) return (Array.Empty<float>(), 0);
                int containerBits = bytesPerSample * 8;
                bool full64 = containerBits >= 64;
                double scale = full64 ? 1.0 / 9223372036854775808.0 : 1.0 / (1L << (containerBits - 1));
                long signMask = full64 ? 0 : 1L << (containerBits - 1);
                long extend = full64 ? 0 : 1L << containerBits;
                for (int i = 0; i < totalSamples; i++)
                {
                    int off = i * bytesPerSample;
                    long v = 0;
                    for (int b = 0; b < bytesPerSample; b++)
                        v |= (long)bytes[off + b] << (8 * b);
                    if (signMask != 0 && (v & signMask) != 0) v -= extend;   // sign-extend from the container
                    output[i] = (float)(v * scale);
                }
                break;
            }
        }

        return (output, frames);
    }

    /// <summary>
    /// Decode interleaved integer PCM samples that have already been read into a
    /// caller-managed int buffer (used by decoders that produce per-channel ints,
    /// e.g. FLAC). <paramref name="samples"/> are normalized by 2^(bits-1).
    /// </summary>
    public static float NormalizeInt(int sample, int bitsPerSample)
    {
        float scale = 1.0f / (1 << (bitsPerSample - 1));
        return sample * scale;
    }
}
