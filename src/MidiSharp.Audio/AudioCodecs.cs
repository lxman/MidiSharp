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
}
