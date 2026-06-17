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
    /// Memory-map SF2 sample data instead of reading it into a managed byte[], keeping the sample
    /// pool off the GC heap. On by default: a cold-start glitch check (a full piece rendered with
    /// pages dropped from cache) stayed well under the audio deadline, and NoteOn issues an OS
    /// prefetch for the sample about to play. Only applies to file-backed SF2 loads ≤ 2 GB, with an
    /// automatic managed fallback otherwise. Disable for streams or memory-constrained callers.
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

    /// <summary>
    /// Decode a referenced sample synchronously on its first use instead of returning silence while a
    /// background decode runs. The lazy path exists so the real-time audio thread never blocks on a
    /// (tens-of-ms) FLAC/Vorbis decode — but an offline render pulls blocks far faster than real time,
    /// so first-hit notes lose the decode race and come out attack-clipped (non-deterministic per run).
    /// Set this for WAV export / any non-real-time render so every note is present and the output is
    /// reproducible. Default false (real-time playback). Currently honored by the SFZ sample source.
    /// </summary>
    public bool BlockingSampleDecode { get; init; }
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
