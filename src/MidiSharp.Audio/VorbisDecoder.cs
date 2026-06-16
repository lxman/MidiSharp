using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using NVorbis;

namespace MidiSharp.Audio;

/// <summary>
/// Ogg/Vorbis decoder, wrapping the vendored NVorbis reader. Serves both the
/// eager file path (SFZ <c>.ogg</c> samples, via <see cref="Decode"/>) and SF3's
/// lazy per-sample path (via the static <see cref="DecodePcm"/> / <see cref="Peek"/>
/// primitives), so there is a single Vorbis implementation behind every caller.
/// </summary>
public sealed class VorbisDecoder : IAudioDecoder
{
    public string Name => "Ogg/Vorbis";

    public bool CanDecode(ReadOnlySpan<byte> header, string? pathHint)
    {
        if (header.Length >= 4 && header[0] == 'O' && header[1] == 'g' && header[2] == 'g' && header[3] == 'S')
            return true;
        return pathHint != null && pathHint.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    public AudioInfo Peek(ReadOnlySpan<byte> data)
    {
        // Vorbis frame count comes from the final page's granule position, so this needs the whole
        // file (a prefix yields FrameCount 0 → the caller retries with the full bytes).
        try
        {
            var arr = data.ToArray();
            using var ms = new MemoryStream(arr, writable: false);
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            long total = reader.TotalSamples;
            return new AudioInfo
            {
                Channels = reader.Channels,
                SampleRate = reader.SampleRate,
                FrameCount = total > 0 ? total : 0,
                RootKey = -1,
                LoopStartFrame = -1,
                LoopEndFrame = -1,
            };
        }
        catch { return AudioInfo.None; }
    }

    public DecodedAudio Decode(byte[] data)
    {
        var samples = DecodePcm(data, out int channels, out int sampleRate, out long frames);
        return new DecodedAudio
        {
            Channels = channels,
            SampleRate = sampleRate,
            BitsPerSample = 16,   // Vorbis is internally float; nominal for diagnostics.
            Samples = samples,
            FrameCount = frames,
            RootKey = -1,         // Ogg carries no instrument root/loop metadata we use.
        };
    }

    /// <summary>
    /// Decode a complete Ogg Vorbis bitstream (or byte slice) to interleaved
    /// float32. Used eagerly by SFZ and lazily, per-sample, by SF3.
    /// </summary>
    public static float[] DecodePcm(ReadOnlyMemory<byte> ogg, out int channels, out int sampleRate, out long frames)
    {
        if (ogg.Length == 0) { channels = 1; sampleRate = 44100; frames = 0; return Array.Empty<float>(); }

        var seg = AsSegment(ogg);
        using var ms = new MemoryStream(seg.Array!, seg.Offset, seg.Count, writable: false, publiclyVisible: false);
        using var reader = new VorbisReader(ms, closeOnDispose: false);

        channels = reader.Channels;
        sampleRate = reader.SampleRate;
        long total = reader.TotalSamples;

        float[] result;
        if (total <= 0 || total > int.MaxValue / Math.Max(1, channels))
        {
            result = DecodeIncremental(reader);
        }
        else
        {
            var buf = new float[total * channels];
            int read = reader.ReadSamples(buf, 0, buf.Length);
            if (read < buf.Length) { Array.Resize(ref buf, read); }
            result = buf;
        }

        frames = channels > 0 ? result.Length / channels : 0;
        return result;
    }

    /// <summary>
    /// Decode an Ogg Vorbis bitstream into a caller-provided buffer (e.g. one rented from
    /// <see cref="System.Buffers.ArrayPool{T}"/>), writing up to <paramref name="maxFloats"/>
    /// interleaved float samples. Returns the number of floats actually written. Used by SF3's
    /// pooled per-sample cache to avoid allocating a fresh array on every decode.
    /// </summary>
    public static int DecodePcmInto(ReadOnlyMemory<byte> ogg, float[] dest, int maxFloats,
        out int channels, out int sampleRate)
    {
        if (ogg.Length == 0 || maxFloats <= 0) { channels = 1; sampleRate = 44100; return 0; }

        var seg = AsSegment(ogg);
        using var ms = new MemoryStream(seg.Array!, seg.Offset, seg.Count, writable: false, publiclyVisible: false);
        using var reader = new VorbisReader(ms, closeOnDispose: false);

        channels = reader.Channels;
        sampleRate = reader.SampleRate;

        var cap = Math.Min(maxFloats, dest.Length);
        var total = 0;
        while (total < cap)
        {
            var got = reader.ReadSamples(dest, total, cap - total);
            if (got <= 0) break;
            total += got;
        }
        return total;
    }

    /// <summary>Read channel count and frame count from the headers without decoding audio.</summary>
    public static void Peek(ReadOnlyMemory<byte> ogg, out int channels, out long frames)
    {
        if (ogg.Length == 0) { channels = 1; frames = 0; return; }
        var seg = AsSegment(ogg);
        using var ms = new MemoryStream(seg.Array!, seg.Offset, seg.Count, writable: false, publiclyVisible: false);
        using var reader = new VorbisReader(ms, closeOnDispose: false);
        channels = reader.Channels;
        frames = reader.TotalSamples;
    }

    private static ArraySegment<byte> AsSegment(ReadOnlyMemory<byte> m) =>
        MemoryMarshal.TryGetArray(m, out var seg) ? seg : new ArraySegment<byte>(m.ToArray());

    private static float[] DecodeIncremental(VorbisReader reader)
    {
        const int Chunk = 4096;
        var tmp = new float[Chunk * reader.Channels];
        var accum = new List<float>();
        int got;
        while ((got = reader.ReadSamples(tmp, 0, tmp.Length)) > 0)
            for (int i = 0; i < got; i++) accum.Add(tmp[i]);
        return accum.ToArray();
    }
}
