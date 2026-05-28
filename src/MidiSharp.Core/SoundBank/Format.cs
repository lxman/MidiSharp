using System;

namespace MidiSharp.SoundBank;

/// <summary>
/// Source format of a loaded <see cref="SoundBank"/>. Set by the loader for
/// diagnostics and round-trip awareness — the synth itself never branches on it.
/// </summary>
public enum SoundBankFormat
{
    Sf2,
    Sf3,
    Sfz,
    Dls,
}

/// <summary>
/// Tunables passed to <c>SoundBankLoader.Load</c>. Defaults are tuned for
/// desktop use; mobile callers will want to lower <see cref="DecodedSampleCacheBytes"/>.
/// </summary>
public sealed class SoundBankLoadOptions
{
    /// <summary>
    /// Memory-map sample data when possible. Disable for streams or non-file
    /// sources. Default true.
    /// </summary>
    public bool MemoryMapSamples { get; init; } = true;

    /// <summary>
    /// Maximum RAM (bytes) for decoded sample cache. Only meaningful for SF3
    /// (Vorbis must be decoded — can't be mmap'd directly). Default 64 MB.
    /// </summary>
    public long DecodedSampleCacheBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Touch the first page of every sample at load time so subsequent NoteOns
    /// don't fault during the audio callback. Cheap insurance; default true.
    /// </summary>
    public bool WarmSampleFirstPages { get; init; } = true;

    /// <summary>
    /// Issue OS prefetch hints for samples that just started playing. Reduces
    /// page-fault audio glitches on flash storage. Default true.
    /// </summary>
    public bool PrefetchActiveSamples { get; init; } = true;
}

/// <summary>
/// Raised when a sound-bank file has the right magic bytes but is otherwise
/// malformed — corrupted RIFF chunks, missing referenced WAVs, bad Vorbis
/// headers, etc.
/// </summary>
public sealed class SoundBankLoadException : Exception
{
    public SoundBankLoadException(string message) : base(message) { }
    public SoundBankLoadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Raised when a file's magic bytes don't match any registered format.
/// </summary>
public sealed class UnsupportedFormatException : Exception
{
    public UnsupportedFormatException(string message) : base(message) { }
    public UnsupportedFormatException(string message, Exception inner) : base(message, inner) { }
}
