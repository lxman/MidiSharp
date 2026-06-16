using System;
using System.Collections.Generic;
using System.IO;

namespace MidiSharp.Audio;

/// <summary>
/// The public entry point for decoding an audio sample file to normalized
/// float32. Callers don't choose a format — the dispatcher sniffs the header
/// (with an optional path/extension hint) and routes to the right
/// <see cref="IAudioDecoder"/>. One seam for every sound-bank loader.
/// </summary>
public static class AudioCodecs
{
    // Order matters only for ambiguity; these four have disjoint magic bytes.
    private static readonly IAudioDecoder[] Decoders =
    {
        new WavDecoder(),
        new AiffDecoder(),
        new FlacDecoder(),
        new VorbisDecoder(),
    };

    /// <summary>Decode a file from disk.</summary>
    public static DecodedAudio Decode(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path must be non-empty", nameof(path));
        return Decode(File.ReadAllBytes(path), path);
    }

    /// <summary>Decode a fully-buffered file. <paramref name="pathHint"/> breaks magic-byte ties via extension.</summary>
    public static DecodedAudio Decode(byte[] data, string? pathHint = null)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var header = new ReadOnlySpan<byte>(data, 0, Math.Min(16, data.Length));
        foreach (var decoder in Decoders)
            if (decoder.CanDecode(header, pathHint))
                return decoder.Decode(data);

        throw new AudioDecodeException(
            pathHint != null
                ? $"Unrecognized audio format: '{pathHint}'"
                : "Unrecognized audio format (no decoder matched the header)");
    }

    /// <summary>True if any registered decoder recognizes the file (cheap header sniff).</summary>
    public static bool CanDecode(ReadOnlySpan<byte> header, string? pathHint)
    {
        foreach (var decoder in Decoders)
            if (decoder.CanDecode(header, pathHint))
                return true;
        return false;
    }

    /// <summary>
    /// Read a sample file's metadata (frames, rate, channels, loop) from its header without decoding
    /// the audio — for lazy sample sources that decode on demand. Reads only a prefix for formats
    /// whose length lives in the header (WAV/FLAC/AIFF); falls back to the whole file when the prefix
    /// is insufficient (Vorbis, whose length is at the end of the stream).
    /// </summary>
    public static AudioInfo Peek(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path must be non-empty", nameof(path));

        const int PrefixBytes = 256 * 1024;
        using var fs = File.OpenRead(path);
        var fileLen = fs.Length;
        var prefixLen = (int)Math.Min(fileLen, PrefixBytes);
        var prefix = new byte[prefixLen];
        ReadFully(fs, prefix, prefixLen);

        var header = new ReadOnlySpan<byte>(prefix, 0, Math.Min(16, prefixLen));
        IAudioDecoder? match = null;
        foreach (var d in Decoders)
            if (d.CanDecode(header, path)) { match = d; break; }
        if (match == null) return AudioInfo.None;

        var info = match.Peek(prefix);
        if (info.FrameCount > 0 || prefixLen >= fileLen) return info;   // got it, or already had everything

        // Prefix didn't carry the length (e.g. Vorbis) — read the whole file and retry.
        fs.Position = 0;
        var all = new byte[fileLen];
        ReadFully(fs, all, (int)fileLen);
        return match.Peek(all);
    }

    private static void ReadFully(Stream s, byte[] buf, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = s.Read(buf, read, count - read);
            if (n <= 0) break;
            read += n;
        }
    }
}
