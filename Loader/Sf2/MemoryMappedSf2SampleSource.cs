using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MidiSharp.Audio;
using MidiSharp.SoundBank;

namespace Loader.Sf2;

/// <summary>
/// Reads SF2 samples and converts int16 → float on the fly.
/// </summary>
/// <remarks>
/// Backed by a zero-copy <see cref="ReadOnlyMemory{T}"/> view over the SF2 file's <c>smpl</c>
/// chunk bytes — no second managed copy of the sample pool is made, so a large font no longer
/// costs ~2× its sample size on the heap. The backing <c>byte[]</c> stays alive for as long as
/// this source holds the <see cref="ReadOnlyMemory{T}"/>, so it safely outlives the parsed
/// <c>SoundFont</c> and remains valid on the audio thread.
/// <para>
/// On little-endian platforms (every real .NET target) reads are a direct reinterpret of the
/// file bytes via <see cref="MemoryMarshal.Cast{TFrom,TTo}(ReadOnlySpan{TFrom})"/>. The big-endian
/// branch reads each frame as little-endian on the fly (SF2 sample data is always LE on disk),
/// which is also copy-free.
/// </para>
/// <para>
/// A future step can swap the backing store for an mmap'd view over the file (the public API and
/// per-sample addressing stay the same — only the source of the int16 bytes changes).
/// </para>
/// </remarks>
internal sealed class MemoryMappedSf2SampleSource : ISampleSource
{
    private readonly ReadOnlyMemory<byte> _smpl;   // raw smpl-chunk bytes (int16 LE frames)
    private readonly ReadOnlyMemory<byte> _sm24;   // optional 24-bit LS bytes, 1/frame; empty for 16-bit fonts
    private readonly SampleEntry[] _entries;
    private readonly SampleMetadata[] _metadata;
    private readonly IDisposable? _backingOwner;   // mmap view to release on dispose; null for managed backing
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
        ReadOnlyMemory<byte> smplBytes,
        IReadOnlyList<SampleMetadata> metadata,
        IReadOnlyList<(long AbsoluteStart, long LengthFrames)> entries,
        IDisposable? backingOwner = null,
        ReadOnlyMemory<byte> sm24Bytes = default)
    {
        if (metadata.Count != entries.Count)
            throw new ArgumentException("metadata and entries must have the same count");

        _smpl = smplBytes;
        // sm24 carries one low byte per frame (optionally +1 even-pad). Need at least one-per-frame to
        // index it safely; otherwise ignore it and stay on the 16-bit fast path.
        _sm24 = sm24Bytes.Length >= smplBytes.Length / 2 ? sm24Bytes : default;
        _backingOwner = backingOwner;
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

        long sourceStart = entry.AbsoluteStart + frameOffset;   // in int16 frames
        const float Scale = 1.0f / 32768.0f;

        if (!_sm24.IsEmpty)
        {
            // 24-bit font (sm24 present): combine the smpl high 16 bits with the sm24 low byte into a
            // signed 24-bit sample and normalize by 2^23. float32's 24-bit mantissa represents this
            // exactly. Only taken when the font actually ships 24-bit data, so 16-bit fonts are
            // byte-identical to the fast path below.
            const float Scale24 = 1.0f / 8388608.0f;
            var hi = _smpl.Span.Slice((int)sourceStart * 2, framesToRead * 2);
            var lo = _sm24.Span.Slice((int)sourceStart, framesToRead);
            for (int i = 0; i < framesToRead; i++)
            {
                int sample24 = (BinaryPrimitives.ReadInt16LittleEndian(hi.Slice(i * 2, 2)) << 8) | lo[i];
                dest[i] = sample24 * Scale24;
            }
            return framesToRead;
        }

        if (BitConverter.IsLittleEndian)
        {
            // Direct reinterpret of the file bytes as int16 — zero copy — then SIMD int16→float.
            var src = MemoryMarshal.Cast<byte, short>(_smpl.Span).Slice((int)sourceStart, framesToRead);
            SampleConvert.Int16ToFloat(src, dest, Scale);
        }
        else
        {
            // Big-endian host: SF2 data on disk is little-endian, so read each frame explicitly.
            var bytes = _smpl.Span.Slice((int)sourceStart * 2, framesToRead * 2);
            for (int i = 0; i < framesToRead; i++)
                dest[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2)) * Scale;
        }

        return framesToRead;
    }

    public void PrepareSample(int sampleId)
    {
        // For a memory-mapped backing, ask the OS to page this sample in ahead of playback so the
        // first read on the audio thread doesn't fault synchronously. No-op for a managed byte[]
        // (already resident) — _backingOwner is only IPrefetchable when mmap-backed.
        if (_backingOwner is not IPrefetchable pf) return;
        var e = _entries[sampleId];
        long byteStart = e.AbsoluteStart * 2;
        long byteLen = e.LengthFrames * 2;
        if (byteStart < 0 || byteLen <= 0 || byteStart >= _smpl.Length) return;
        byteLen = Math.Min(byteLen, _smpl.Length - byteStart);   // clamp; never fault on a bad header
        pf.Prefetch(_smpl.Slice((int)byteStart, (int)byteLen));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Releases the memory-mapped view when the backing is mmap'd; no-op for a managed byte[].
        // Must run only after the audio output has stopped (no in-flight ReadFrames) — disposing an
        // mmap view out from under a live audio-thread read is a native use-after-free.
        _backingOwner?.Dispose();
    }
}
