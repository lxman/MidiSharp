using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using MidiSharp.Audio.Internal;

namespace MidiSharp.Audio;

/// <summary>
/// Clean-room FLAC decoder. Implements the subset of the FLAC format used by
/// real instrument samples: STREAMINFO metadata, fixed and LPC subframes,
/// CONSTANT/VERBATIM subframes, Rice-coded residuals (both parameter widths and
/// the escape code), wasted bits, and left/right/mid-side stereo decorrelation.
/// Other metadata blocks are skipped; CRCs are not verified.
/// </summary>
/// <remarks>
/// Reference: the public FLAC format specification (xiph.org/flac/format.html).
/// Decodes eagerly to interleaved float32 in [-1, 1].
/// </remarks>
public sealed class FlacDecoder : IAudioDecoder
{
    public string Name => "FLAC";

    public bool CanDecode(ReadOnlySpan<byte> header, string? pathHint)
    {
        if (header.Length >= 4 && header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C')
            return true;
        return pathHint != null && pathHint.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);
    }

    public AudioInfo Peek(ReadOnlySpan<byte> data)
    {
        // STREAMINFO is required to be the first metadata block (right after the "fLaC" marker),
        // so frames/rate/channels are available in the first ~50 bytes — no audio decode needed.
        if (data.Length < 4 + 4 + 34 || data[0] != 'f' || data[1] != 'L' || data[2] != 'a' || data[3] != 'C')
            return AudioInfo.None;
        int type = data[4] & 0x7F;
        if (type != 0) return AudioInfo.None;   // first block isn't STREAMINFO → malformed for our purposes
        var s = data.Slice(8, 34);
        int sampleRate = (s[10] << 12) | (s[11] << 4) | (s[12] >> 4);
        int channels = ((s[12] >> 1) & 0x07) + 1;
        long total = ((long)(s[13] & 0x0F) << 32) | ((long)s[14] << 24) |
                     ((long)s[15] << 16) | ((long)s[16] << 8) | s[17];
        return new AudioInfo
        {
            Channels = channels,
            SampleRate = sampleRate <= 0 ? 44100 : sampleRate,
            FrameCount = total,
            RootKey = -1,
            LoopStartFrame = -1,
            LoopEndFrame = -1,
        };
    }

    public DecodedAudio Decode(byte[] data)
    {
        if (data.Length < 4 || data[0] != 'f' || data[1] != 'L' || data[2] != 'a' || data[3] != 'C')
            throw new AudioDecodeException("Not a FLAC stream");

        // ── Metadata blocks (byte-aligned). Parse STREAMINFO, skip the rest. ──
        int pos = 4;
        int sampleRate = 0, channels = 0, bitsPerSample = 0;
        long totalSamples = 0;
        bool gotStreamInfo = false;

        while (pos + 4 <= data.Length)
        {
            byte h = data[pos];
            bool last = (h & 0x80) != 0;
            int type = h & 0x7F;
            int len = (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            int body = pos + 4;
            if (body + len > data.Length) len = data.Length - body;

            if (type == 0 && len >= 34)  // STREAMINFO
            {
                var s = new ReadOnlySpan<byte>(data, body, 34);
                sampleRate = (s[10] << 12) | (s[11] << 4) | (s[12] >> 4);
                channels = ((s[12] >> 1) & 0x07) + 1;
                bitsPerSample = (((s[12] & 1) << 4) | (s[13] >> 4)) + 1;
                totalSamples = ((long)(s[13] & 0x0F) << 32) | ((long)s[14] << 24) |
                               ((long)s[15] << 16) | ((long)s[16] << 8) | s[17];
                gotStreamInfo = true;
            }

            pos = body + len;
            if (last) break;
        }

        if (!gotStreamInfo || channels < 1 || bitsPerSample < 1)
            throw new AudioDecodeException("FLAC stream missing/invalid STREAMINFO");

        // ── Audio frames ──
        var channelData = new List<int>[channels];
        for (int c = 0; c < channels; c++)
            channelData[c] = new List<int>(totalSamples > 0 ? (int)Math.Min(totalSamples, int.MaxValue) : 0);

        var reader = new BitReader(data, pos);
        long produced = 0;
        while (!reader.AtEnd && (totalSamples == 0 || produced < totalSamples))
        {
            int frameSamples = DecodeFrame(reader, channels, bitsPerSample, sampleRate, channelData);
            if (frameSamples <= 0) break;
            produced += frameSamples;
        }

        // ── Interleave + normalize ──
        long frames = channelData[0].Count;
        for (int c = 1; c < channels; c++) frames = Math.Min(frames, channelData[c].Count);
        float scale = 1.0f / (1u << (bitsPerSample - 1));
        var samples = new float[frames * channels];
        for (long i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
                samples[i * channels + c] = channelData[c][(int)i] * scale;

        return new DecodedAudio
        {
            Channels = channels,
            SampleRate = sampleRate <= 0 ? 44100 : sampleRate,
            BitsPerSample = bitsPerSample,
            Samples = samples,
            FrameCount = frames,
            // FLAC carries no instrument root/loop metadata we use; SFZ opcodes supply those.
            RootKey = -1,
        };
    }

    // ── Frame ───────────────────────────────────────────────────────────

    private static int DecodeFrame(BitReader r, int channels, int streamBps, int streamRate, List<int>[] output)
    {
        // Frame header. Sync = 0b11111111111110 (14 bits).
        uint sync = r.ReadBits(14);
        if (sync != 0x3FFE) throw new AudioDecodeException("FLAC frame sync lost");
        r.ReadBits(1);                       // reserved
        r.ReadBits(1);                       // blocking strategy (we infer block size directly)

        int blockSizeCode = (int)r.ReadBits(4);
        int sampleRateCode = (int)r.ReadBits(4);
        int channelAssignment = (int)r.ReadBits(4);
        int sampleSizeCode = (int)r.ReadBits(3);
        r.ReadBits(1);                       // reserved

        SkipCodedNumber(r);                  // UTF-8-style frame/sample number

        int blockSize = blockSizeCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => (int)r.ReadBits(8) + 1,
            7 => (int)r.ReadBits(16) + 1,
            >= 8 and <= 15 => 256 << (blockSizeCode - 8),
            _ => throw new AudioDecodeException("Reserved FLAC block size code"),
        };

        // Sample-rate code may pull extra bytes; value unused (we trust STREAMINFO).
        switch (sampleRateCode)
        {
            case 12: r.ReadBits(8); break;
            case 13: case 14: r.ReadBits(16); break;
        }

        int bps = sampleSizeCode switch
        {
            0 => streamBps,
            1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24,
            _ => streamBps,
        };

        r.ReadBits(8);                       // CRC-8 (not verified)

        // Per-channel subframes. Side channels carry one extra bit of range.
        int chCount = channelAssignment < 8 ? channelAssignment + 1 : 2;
        var block = new int[chCount][];
        for (int c = 0; c < chCount; c++)
        {
            int subBps = bps + SideExtraBits(channelAssignment, c);
            block[c] = DecodeSubframe(r, blockSize, subBps);
        }

        r.AlignToByte();
        r.ReadBits(16);                      // CRC-16 (not verified)

        Decorrelate(channelAssignment, block, blockSize);

        // FLAC always emits `channels` channels per frame; append to accumulators.
        for (int c = 0; c < channels && c < block.Length; c++)
            output[c].AddRange(block[c]);
        return blockSize;
    }

    private static int SideExtraBits(int assignment, int channel) => assignment switch
    {
        8 => channel == 1 ? 1 : 0,   // left/side: side is ch1
        9 => channel == 0 ? 1 : 0,   // right/side: side is ch0
        10 => channel == 1 ? 1 : 0,  // mid/side: side is ch1
        _ => 0,
    };

    private static void Decorrelate(int assignment, int[][] block, int n)
    {
        switch (assignment)
        {
            case 8: // left/side: ch0=left, ch1=side(=left-right) → right = left - side
                for (int i = 0; i < n; i++) block[1][i] = block[0][i] - block[1][i];
                break;
            case 9: // right/side: ch0=side(=left-right), ch1=right → left = right + side
                for (int i = 0; i < n; i++) block[0][i] = block[1][i] + block[0][i];
                break;
            case 10: // mid/side: ch0=mid, ch1=side
                for (int i = 0; i < n; i++)
                {
                    int side = block[1][i];
                    int mid = (block[0][i] << 1) | (side & 1);
                    block[0][i] = (mid + side) >> 1;
                    block[1][i] = (mid - side) >> 1;
                }
                break;
            // 0-7: independent channels — nothing to do.
        }
    }

    // ── Subframe ──────────────────────────────────────────────────────────

    private static int[] DecodeSubframe(BitReader r, int blockSize, int bps)
    {
        r.ReadBits(1);                       // mandatory zero padding bit
        int type = (int)r.ReadBits(6);

        int wasted = 0;
        if (r.ReadBits(1) == 1)              // wasted-bits flag → unary (count of zeros)+1
            wasted = r.ReadUnary() + 1;
        int effBps = bps - wasted;

        var samples = new int[blockSize];

        if (type == 0)                       // CONSTANT
        {
            int v = r.ReadBitsSigned(effBps);
            for (int i = 0; i < blockSize; i++) samples[i] = v;
        }
        else if (type == 1)                  // VERBATIM
        {
            for (int i = 0; i < blockSize; i++) samples[i] = r.ReadBitsSigned(effBps);
        }
        else if (type >= 8 && type <= 12)    // FIXED, order = type - 8
        {
            DecodeFixed(r, samples, blockSize, effBps, order: type - 8);
        }
        else if (type >= 32)                 // LPC, order = (type & 0x1F) + 1
        {
            DecodeLpc(r, samples, blockSize, effBps, order: (type & 0x1F) + 1);
        }
        else
        {
            throw new AudioDecodeException($"Reserved FLAC subframe type {type}");
        }

        if (wasted > 0)
            for (int i = 0; i < blockSize; i++) samples[i] <<= wasted;

        return samples;
    }

    private static void DecodeFixed(BitReader r, int[] s, int n, int bps, int order)
    {
        for (int i = 0; i < order; i++) s[i] = r.ReadBitsSigned(bps);
        DecodeResidual(r, s, n, order);

        // Restore via the fixed polynomial predictors.
        switch (order)
        {
            case 0: break;
            case 1: for (int i = 1; i < n; i++) s[i] += s[i - 1]; break;
            case 2: for (int i = 2; i < n; i++) s[i] += 2 * s[i - 1] - s[i - 2]; break;
            case 3: for (int i = 3; i < n; i++) s[i] += 3 * s[i - 1] - 3 * s[i - 2] + s[i - 3]; break;
            case 4: for (int i = 4; i < n; i++) s[i] += 4 * s[i - 1] - 6 * s[i - 2] + 4 * s[i - 3] - s[i - 4]; break;
        }
    }

    private static void DecodeLpc(BitReader r, int[] s, int n, int bps, int order)
    {
        for (int i = 0; i < order; i++) s[i] = r.ReadBitsSigned(bps);

        int rawPrecision = (int)r.ReadBits(4);          // stores precision-1; 0b1111 is invalid
        if (rawPrecision == 0xF) throw new AudioDecodeException("Invalid FLAC LPC precision");
        int precision = rawPrecision + 1;
        int shift = r.ReadBitsSigned(5);
        if (shift < 0) shift = 0;                      // negative shift is malformed; clamp

        var coeff = new int[order];
        for (int i = 0; i < order; i++) coeff[i] = r.ReadBitsSigned(precision);

        DecodeResidual(r, s, n, order);

        for (int i = order; i < n; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++) sum += (long)coeff[j] * s[i - 1 - j];
            s[i] += (int)(sum >> shift);
        }
    }

    // ── Residual (Rice partitions) ──────────────────────────────────────────

    private static void DecodeResidual(BitReader r, int[] s, int n, int predictorOrder)
    {
        int method = (int)r.ReadBits(2);
        if (method > 1) throw new AudioDecodeException($"Reserved FLAC residual coding method {method}");
        int paramBits = method == 0 ? 4 : 5;
        int escape = method == 0 ? 0xF : 0x1F;

        int partitionOrder = (int)r.ReadBits(4);
        int partitions = 1 << partitionOrder;
        int partitionSamples = n >> partitionOrder;

        int idx = predictorOrder;
        for (int p = 0; p < partitions; p++)
        {
            int count = (p == 0) ? partitionSamples - predictorOrder : partitionSamples;
            int rice = (int)r.ReadBits(paramBits);

            if (rice == escape)
            {
                int bits = (int)r.ReadBits(5);
                for (int i = 0; i < count; i++)
                    s[idx++] = bits == 0 ? 0 : r.ReadBitsSigned(bits);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    int q = r.ReadUnary();
                    uint low = rice > 0 ? r.ReadBits(rice) : 0;
                    uint u = ((uint)q << rice) | low;
                    s[idx++] = (int)(u >> 1) ^ -(int)(u & 1);   // zig-zag → signed
                }
            }
        }
    }

    // ── Frame/sample number: UTF-8-style variable length; value unused ──────

    private static void SkipCodedNumber(BitReader r)
    {
        uint first = r.ReadBits(8);
        if ((first & 0x80) == 0) return;          // 1 byte
        int extra = 0;
        uint b = first;
        while ((b & 0x80) != 0) { extra++; b = (b << 1) & 0xFF; }
        // `extra` = total byte count for 0b110x..(2) through 0b1111110x(7); read the rest.
        for (int i = 1; i < extra; i++) r.ReadBits(8);
    }
}
