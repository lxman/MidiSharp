using System;
using System.Buffers;
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
/// <para>
/// Decoded buffers are rented from <see cref="ArrayPool{T}"/> and returned on eviction/dispose,
/// so repeated decode→evict cycles don't churn the large-object heap. Because a returned buffer can
/// be reused immediately, reads copy out of the cache <em>under the cache lock</em> — eviction
/// (which returns the buffer) and a concurrent read can never overlap, so a buffer is never reused
/// while still being read. The expensive Vorbis decode itself runs outside the lock.
/// </para>
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
        public int Length;          // valid floats (Data may be longer when pool-rented)
        public bool Pooled;         // Data came from ArrayPool and must be returned when dropped
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
        var channels = _entries[sampleId].Channels;

        // Fast path: cache hit — refresh LRU and copy, all under the lock.
        lock (_cacheLock)
            if (_cache.TryGetValue(sampleId, out var hit))
                return CopyFromNode(hit, sampleId, frameOffset, dest, channels, refreshLru: true);

        // Miss: decode outside the lock so concurrent decodes don't serialize, then insert + copy.
        var (data, length, pooled) = Decode(sampleId);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(sampleId, out var raced))   // another thread decoded it meanwhile
            {
                if (pooled) ArrayPool<float>.Shared.Return(data);
                return CopyFromNode(raced, sampleId, frameOffset, dest, channels, refreshLru: true);
            }
            EvictUntilBudgetFits(length * 4L);
            var node = new CacheNode { Data = data, Length = length, Pooled = pooled, LruNode = _lru.AddLast(sampleId) };
            _cache[sampleId] = node;
            _cachedBytes += length * 4L;
            return CopyFromNode(node, sampleId, frameOffset, dest, channels, refreshLru: false);
        }
    }

    // Must be called holding _cacheLock — copies under the lock so the buffer can't be returned
    // to the pool (via eviction) while this read is in progress.
    private int CopyFromNode(CacheNode node, int sampleId, long frameOffset, Span<float> dest, int channels, bool refreshLru)
    {
        if (refreshLru && node.LruNode != null)
        {
            _lru.Remove(node.LruNode);
            node.LruNode = _lru.AddLast(sampleId);
        }

        long frameCount = channels > 0 ? node.Length / channels : 0;
        if (frameOffset < 0 || frameOffset >= frameCount) return 0;
        int framesAvailable = (int)Math.Min(frameCount - frameOffset, dest.Length);
        node.Data.AsSpan((int)(frameOffset * channels), framesAvailable * channels).CopyTo(dest);
        return framesAvailable;
    }

    public void PrepareSample(int sampleId)
    {
        // Synchronous decode-on-hint so the sample is cached before it plays.
        lock (_cacheLock)
            if (_cache.ContainsKey(sampleId)) return;

        var (data, length, pooled) = Decode(sampleId);
        lock (_cacheLock)
        {
            if (_cache.ContainsKey(sampleId)) { if (pooled) ArrayPool<float>.Shared.Return(data); return; }
            EvictUntilBudgetFits(length * 4L);
            _cache[sampleId] = new CacheNode { Data = data, Length = length, Pooled = pooled, LruNode = _lru.AddLast(sampleId) };
            _cachedBytes += length * 4L;
        }
    }

    // Holds _cacheLock. Returns evicted pooled buffers to the pool.
    private void EvictUntilBudgetFits(long incoming)
    {
        while (_cachedBytes + incoming > _cacheBudgetBytes && _lru.Count > 0)
        {
            int victim = _lru.First!.Value;
            _lru.RemoveFirst();
            if (_cache.TryGetValue(victim, out var victimNode))
            {
                _cachedBytes -= victimNode.Length * 4L;
                _cache.Remove(victim);
                if (victimNode.Pooled) ArrayPool<float>.Shared.Return(victimNode.Data);
            }
        }
    }

    private (float[] data, int length, bool pooled) Decode(int sampleId)
    {
        var entry = _entries[sampleId];
        if (entry.ByteLength <= 0) return (Array.Empty<float>(), 0, false);

        var slice = _smplBytes.Slice(entry.ByteStart, entry.ByteLength);

        // Known length → decode straight into a pooled buffer (the common case).
        VorbisDecoder.Peek(slice, out int channels, out long frames);
        if (frames > 0 && frames <= int.MaxValue / Math.Max(1, channels))
        {
            int floats = (int)(frames * channels);
            var buf = ArrayPool<float>.Shared.Rent(floats);
            int read = VorbisDecoder.DecodePcmInto(slice, buf, floats, out _, out _);
            return (buf, read, true);
        }

        // Unknown length (rare) → fall back to the allocating decode; not pooled.
        var arr = VorbisDecoder.DecodePcm(slice, out _, out _, out _);
        return (arr, arr.Length, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_cacheLock)
        {
            foreach (var kv in _cache)
                if (kv.Value.Pooled) ArrayPool<float>.Shared.Return(kv.Value.Data);
            _cache.Clear();
            _lru.Clear();
            _cachedBytes = 0;
        }
    }
}
