using System;

namespace MidiSharp.Audio;

/// <summary>
/// Decodes one audio container format into <see cref="DecodedAudio"/>. Decoders
/// are stateless and registered with <see cref="AudioCodecs"/>, which sniffs the
/// header to pick one. Implementations decode eagerly (whole file → float[]).
/// </summary>
public interface IAudioDecoder
{
    /// <summary>Human-readable format name, for diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// True if this decoder recognizes the data, judged from the leading bytes
    /// (≥ 16 available) and an optional path/extension hint.
    /// </summary>
    bool CanDecode(ReadOnlySpan<byte> header, string? pathHint);

    /// <summary>Decode the complete file. Throws <see cref="AudioDecodeException"/> on malformed input.</summary>
    DecodedAudio Decode(byte[] data);

    /// <summary>
    /// Read metadata (frame count, sample rate, channels, and loop/root where present) from the
    /// header without decoding the audio. <paramref name="data"/> may be only a prefix of the file;
    /// return <see cref="AudioInfo.None"/> (FrameCount 0) when it's too short to determine the length,
    /// so the caller can retry with the full file.
    /// </summary>
    AudioInfo Peek(ReadOnlySpan<byte> data);
}

/// <summary>Raised when a recognized format is structurally malformed.</summary>
public sealed class AudioDecodeException : Exception
{
    public AudioDecodeException(string message) : base(message) { }
    public AudioDecodeException(string message, Exception inner) : base(message, inner) { }
}
