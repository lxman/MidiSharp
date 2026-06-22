using System;

namespace MidiSharp.SoundBank;

/// <summary>
/// Per-sample metadata. RAM-resident, immutable, cheap to query. All frame
/// counts are sample-relative (frame 0 = first frame of this sample, not byte
/// 0 of any shared chunk).
/// </summary>
public sealed class SampleMetadata
{
    public string? Name { get; init; }

    public int SampleRate { get; init; }

    /// <summary>1 for SF2/DLS; 1 or 2 for SFZ. Channels=2 means interleaved L/R per frame.</summary>
    public int Channels { get; init; } = 1;

    public long LengthFrames { get; init; }

    public long LoopStartFrames { get; init; }

    public long LoopEndFrames { get; init; }

    /// <summary>0-127; 255 = unpitched (drum hits).</summary>
    public int RootKey { get; init; } = 60;

    /// <summary>The sample's own intrinsic detune, applied at every NoteOn.</summary>
    public double PitchCorrectionCents { get; init; }

    /// <summary>For L/R stereo pairs (SF2 SampleLink); null = standalone.</summary>
    public int? StereoLinkSampleId { get; init; }
}

/// <summary>
/// The synth's audio loop talks to this and only this for sample bytes.
/// Implementations MUST be thread-safe and allocation-free in the hot path.
/// </summary>
public interface ISampleSource : IDisposable
{
    /// <summary>Number of distinct samples in this source.</summary>
    int Count { get; }

    /// <summary>
    /// Read metadata for one sample. Cheap (RAM-resident); safe from any thread.
    /// </summary>
    SampleMetadata Metadata(int sampleId);

    /// <summary>
    /// Read frames from sample <paramref name="sampleId"/> starting at
    /// <paramref name="frameOffset"/> (sample-relative) into <paramref name="dest"/>.
    /// Returns the number of frames actually written (≤ <c>dest.Length</c>).
    /// A short read indicates end-of-sample; the caller wraps to the loop or stops
    /// based on its <see cref="LoopMode"/>.
    /// </summary>
    /// <remarks>
    /// MUST be thread-safe (audio thread + UI thread may call simultaneously),
    /// non-blocking under normal conditions (page faults from cold mmap pages
    /// are acceptable; explicit decode work must be prefetched via
    /// <see cref="PrepareSample"/>), and allocation-free in the hot path.
    /// </remarks>
    int ReadFrames(int sampleId, long frameOffset, Span<float> dest);

    /// <summary>
    /// Hint that <paramref name="sampleId"/> is about to be played. Implementations
    /// may decode it into cache (SF3), issue an OS prefetch (mmap'd SF2/SFZ/DLS),
    /// or no-op. Called from the audio thread on NoteOn — must return quickly.
    /// </summary>
    void PrepareSample(int sampleId);
}

/// <summary>
/// Convenience sample source backed by an in-memory float buffer per sample.
/// Useful for tests, network sources, and as a transitional shim while the
/// synth migrates from <c>float[]</c>-indexed playback to <see cref="ISampleSource"/>.
/// </summary>
/// <remarks>
/// All sample data is kept resident in RAM — no streaming, no eviction. For
/// large banks prefer a memory-mapped implementation.
/// </remarks>
public sealed class PreDecodedFloatSampleSource : ISampleSource
{
    private readonly float[][] _samples;
    private readonly SampleMetadata[] _metadata;

    /// <summary>
    /// Create a source from parallel arrays of sample buffers and metadata.
    /// The arrays must be the same length; <paramref name="samples"/>[i] is the
    /// frame data described by <paramref name="metadata"/>[i].
    /// </summary>
    public PreDecodedFloatSampleSource(float[][] samples, SampleMetadata[] metadata)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        if (metadata == null) throw new ArgumentNullException(nameof(metadata));
        if (samples.Length != metadata.Length)
            throw new ArgumentException("samples and metadata must have the same length");

        _samples = samples;
        _metadata = metadata;
    }

    public int Count => _samples.Length;

    public SampleMetadata Metadata(int sampleId) => _metadata[sampleId];

    public int ReadFrames(int sampleId, long frameOffset, Span<float> dest)
    {
        float[] src = _samples[sampleId];
        int channels = _metadata[sampleId].Channels;

        long firstFloat = frameOffset * channels;
        if (firstFloat >= src.Length) return 0;

        var available = (int)Math.Min(dest.Length, src.Length - firstFloat);
        new ReadOnlySpan<float>(src, (int)firstFloat, available).CopyTo(dest);
        return available / channels;
    }

    public void PrepareSample(int sampleId)
    {
        // No-op: all samples are already resident in RAM.
    }

    public void Dispose()
    {
        // No unmanaged resources to release.
    }
}

/// <summary>
/// A zero-sample placeholder. Used as the default for newly-constructed
/// <see cref="SoundBank"/> instances before a real source is assigned.
/// </summary>
internal sealed class EmptySampleSource : ISampleSource
{
    public static readonly EmptySampleSource Instance = new();
    public int Count => 0;
    public SampleMetadata Metadata(int sampleId) => throw new ArgumentOutOfRangeException(nameof(sampleId));
    public int ReadFrames(int sampleId, long frameOffset, Span<float> dest) => 0;
    public void PrepareSample(int sampleId) { }
    public void Dispose() { }
}
