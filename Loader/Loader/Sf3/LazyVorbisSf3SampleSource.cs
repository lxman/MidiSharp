using System;
using System.Collections.Generic;
using MidiSharp.Audio;

namespace MidiSharp.SoundBank.Sf3;

/// <summary>
/// Decodes SF3 Vorbis-encoded samples on demand and serves them through an
/// LRU cache bounded by <see cref="SoundBankLoadOptions.DecodedSampleCacheBytes"/>.
/// </summary>
/// <remarks>
/// SF3 stores each sample as a complete Ogg Vorbis bitstream in the <c>smpl</c>
/// chunk, addressed by byte offsets in <c>SHDR.Start</c> / <c>SHDR.End</c>.
/// Audio-thread cache misses pay a one-time decode cost (typically a few ms
/// per sample); the result is held in the cache until LRU eviction. Decoded
/// frames are stored interleaved float per channel.
/// </remarks>
internal sealed class LazyVorbisSf3SampleSource : ISampleSource
{
    private readonly ReadOnlyMemory<byte> _smplBytes;
    private readonly Entry[] _entries;
    private readonly SampleMetadata[] _metadata;
    private readonly long _cacheBudgetBytes;

    private readonly object _cacheLock = new();
    private readonly Dictionary<int, CacheNode> _cache = new();
    private readonly LinkedList<int> _lru = new();
    private long _cachedBytes;
    private bool _disposed;

    private readonly struct Entry
    {
        public readonly int ByteStart;
        public readonly int ByteLength;
        public readonly int Channels;
        public Entry(int s, int l, int c) { ByteStart = s; ByteLength = l; Channels = c; }
    }

    private sealed class CacheNode
    {
        public float[] Data = Array.Empty<float>();
        public LinkedListNode<int>? LruNode;
    }

    public int Count => _metadata.Length;
    public SampleMetadata Metadata(int sampleId) => _metadata[sampleId];

    public LazyVorbisSf3SampleSource(
        ReadOnlyMemory<byte> smplBytes,
        IReadOnlyList<SampleMetadata> metadata,
        IReadOnlyList<(int ByteStart, int ByteLength, int Channels)> entries,
        long cacheBudgetBytes)
    {
        if (metadata.Count != entries.Count)
            throw new ArgumentException("metadata and entries must have the same count");

        _smplBytes = smplBytes;
        _metadata = new SampleMetadata[metadata.Count];
        _entries = new Entry[entries.Count];
        for (int i = 0; i < metadata.Count; i++)
        {
            _metadata[i] = metadata[i];
            var e = entries[i];
            _entries[i] = new Entry(e.ByteStart, e.ByteLength, e.Channels);
        }
        _cacheBudgetBytes = cacheBudgetBytes <= 0 ? long.MaxValue : cacheBudgetBytes;
    }

    public int ReadFrames(int sampleId, long frameOffset, Span<float> dest)
    {
        var decoded = GetOrDecode(sampleId);
        int channels = _entries[sampleId].Channels;
        long frameCount = decoded.Length / channels;

        if (frameOffset < 0 || frameOffset >= frameCount) return 0;
        int framesAvailable = (int)Math.Min(frameCount - frameOffset, dest.Length);
        int floatsNeeded = framesAvailable * channels;

        decoded.AsSpan((int)(frameOffset * channels), floatsNeeded).CopyTo(dest);
        return framesAvailable;
    }

    public void PrepareSample(int sampleId)
    {
        // Synchronous decode-on-hint. Background decoding can be added later if
        // audio-thread blocking on NoteOn becomes measurable; for now the decode
        // cost (one-shot per sample, then cached) is amortized across playback.
        GetOrDecode(sampleId);
    }

    private float[] GetOrDecode(int sampleId)
    {
        // Fast path: cache hit. Refresh LRU under lock.
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(sampleId, out var node))
            {
                if (node.LruNode != null)
                {
                    _lru.Remove(node.LruNode);
                    node.LruNode = _lru.AddLast(sampleId);
                }
                return node.Data;
            }
        }

        // Slow path: decode outside the lock so concurrent decodes don't serialize.
        var fresh = Decode(sampleId);

        lock (_cacheLock)
        {
            // Re-check in case another thread decoded the same sample while we were busy.
            if (_cache.TryGetValue(sampleId, out var existing))
            {
                if (existing.LruNode != null)
                {
                    _lru.Remove(existing.LruNode);
                    existing.LruNode = _lru.AddLast(sampleId);
                }
                return existing.Data;
            }

            long addedBytes = fresh.Length * 4L;
            EvictUntilBudgetFits(addedBytes);

            var node = new CacheNode { Data = fresh, LruNode = _lru.AddLast(sampleId) };
            _cache[sampleId] = node;
            _cachedBytes += addedBytes;
            return fresh;
        }
    }

    private void EvictUntilBudgetFits(long incoming)
    {
        while (_cachedBytes + incoming > _cacheBudgetBytes && _lru.Count > 0)
        {
            int victim = _lru.First!.Value;
            _lru.RemoveFirst();
            if (_cache.TryGetValue(victim, out var victimNode))
            {
                _cachedBytes -= victimNode.Data.Length * 4L;
                _cache.Remove(victim);
            }
        }
    }

    private float[] Decode(int sampleId)
    {
        var entry = _entries[sampleId];
        if (entry.ByteLength <= 0) return Array.Empty<float>();

        // Decode the sample's Ogg Vorbis slice through the shared codec primitive.
        var slice = _smplBytes.Slice(entry.ByteStart, entry.ByteLength);
        return VorbisDecoder.DecodePcm(slice, out _, out _, out _);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_cacheLock)
        {
            _cache.Clear();
            _lru.Clear();
            _cachedBytes = 0;
        }
    }
}
