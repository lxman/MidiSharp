using System;
using System.Collections.Generic;

namespace MidiSharp.SoundBank.Sf2;

/// <summary>
/// Reads SF2 samples and converts int16 → float on the fly.
/// </summary>
/// <remarks>
/// Step 2 implementation: backed by an in-memory <c>short[]</c> copy of the
/// SF2 file's <c>smpl</c> chunk. Step 3 replaces the backing store with an
/// mmap'd view over the file (the public API and per-sample addressing stay
/// the same — only the source of the int16 data changes). Until then, the
/// "MemoryMapped" name is aspirational: working set scales with the entire
/// sample pool, not with active polyphony.
/// </remarks>
internal sealed class MemoryMappedSf2SampleSource : ISampleSource
{
    private readonly short[] _smpl;
    private readonly SampleEntry[] _entries;
    private readonly SampleMetadata[] _metadata;
    private bool _disposed;

    private readonly struct SampleEntry
    {
        public readonly long AbsoluteStart;
        public readonly long LengthFrames;

        public SampleEntry(long absoluteStart, long lengthFrames)
        {
            AbsoluteStart = absoluteStart;
            LengthFrames = lengthFrames;
        }
    }

    public MemoryMappedSf2SampleSource(
        short[] smplData,
        IReadOnlyList<SampleMetadata> metadata,
        IReadOnlyList<(long AbsoluteStart, long LengthFrames)> entries)
    {
        if (metadata.Count != entries.Count)
            throw new ArgumentException("metadata and entries must have the same count");

        _smpl = smplData;
        _metadata = new SampleMetadata[metadata.Count];
        _entries = new SampleEntry[entries.Count];
        for (int i = 0; i < metadata.Count; i++)
        {
            _metadata[i] = metadata[i];
            _entries[i] = new SampleEntry(entries[i].AbsoluteStart, entries[i].LengthFrames);
        }
    }

    public int Count => _metadata.Length;

    public SampleMetadata Metadata(int sampleId) => _metadata[sampleId];

    public int ReadFrames(int sampleId, long frameOffset, Span<float> dest)
    {
        var entry = _entries[sampleId];

        if (frameOffset < 0 || frameOffset >= entry.LengthFrames) return 0;

        long available = entry.LengthFrames - frameOffset;
        int framesToRead = (int)Math.Min(available, dest.Length);

        long sourceStart = entry.AbsoluteStart + frameOffset;
        var src = new ReadOnlySpan<short>(_smpl, (int)sourceStart, framesToRead);

        const float Scale = 1.0f / 32768.0f;
        for (int i = 0; i < framesToRead; i++)
        {
            dest[i] = src[i] * Scale;
        }

        return framesToRead;
    }

    public void PrepareSample(int sampleId)
    {
        // No-op while backed by managed memory. Step 3 issues madvise(WILLNEED)
        // for the sample's pages on the mmap'd backing.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // _smpl is a managed array; nothing to release.
    }
}
