using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using MidiSharp.Audio;
using MidiSharp.SoundBank;

namespace MidiSharp.Loader.Sfz;

/// <summary>
/// Lazy sample source for SFZ external sample files. Each referenced file is decoded on first use
/// (folded to mono — the synth voices are mono) and held in an LRU cache bounded by
/// <see cref="SoundBankLoadOptions.DecodedSampleCacheBytes"/>. A large multisampled instrument
/// (e.g. a 700 MB piano of hundreds of FLACs) therefore starts playing immediately and only ever
/// decodes the samples a piece actually touches, with memory capped at the cache budget instead of
/// resident in full.
/// </summary>
/// <remarks>
/// Decoded buffers are rented from <see cref="ArrayPool{T}"/> and returned on eviction/dispose, and
/// reads copy out of the cache under the cache lock so a returned buffer can never be reused mid-read
/// (the same lifetime discipline as the SF3 source). The expensive decode runs outside the lock.
/// </remarks>
internal sealed class SfzSampleSource : ISampleSource
{
    private readonly string[] _paths;
    private readonly SampleMetadata[] _metadata;
    private readonly long _cacheBudgetBytes;
    private readonly bool _blockingDecode;   // offline render: decode synchronously so no note loses the race

    private readonly object _cacheLock = new();
    private readonly Dictionary<int, CacheNode> _cache = new();
    private readonly LinkedList<int> _lru = [];
    private readonly HashSet<int> _decoding = [];   // samples a background decode is in flight for
    private long _cachedBytes;
    private bool _disposed;

    private sealed class CacheNode
    {
        public float[] Data = [];
        public int Length;
        public bool Pooled;
        public LinkedListNode<int>? LruNode;
    }

    public int Count => _metadata.Length;
    public SampleMetadata Metadata(int sampleId) => _metadata[sampleId];

    public SfzSampleSource(IReadOnlyList<string> paths, IReadOnlyList<SampleMetadata> metadata, long cacheBudgetBytes,
        bool blockingDecode = false)
    {
        if (paths.Count != metadata.Count)
            throw new ArgumentException("paths and metadata must have the same count");
        _blockingDecode = blockingDecode;
        _paths = new string[paths.Count];
        _metadata = new SampleMetadata[metadata.Count];
        for (var i = 0; i < paths.Count; i++) { _paths[i] = paths[i]; _metadata[i] = metadata[i]; }
        // Floor the budget so the active working set (many sustained piano voices, each a multi-MB
        // decoded sample) fits without the cache thrashing decode→evict→re-decode under polyphony.
        const long MinBudget = 256L * 1024 * 1024;
        _cacheBudgetBytes = cacheBudgetBytes <= 0 ? long.MaxValue : Math.Max(cacheBudgetBytes, MinBudget);
    }

    public int ReadFrames(int sampleId, long frameOffset, Span<float> dest)
    {
        lock (_cacheLock)
            if (_cache.TryGetValue(sampleId, out CacheNode? hit))
                return CopyFromNode(hit, sampleId, frameOffset, dest, refreshLru: true);

        if (!_blockingDecode)
        {
            // Real-time: kick off a background decode (FLAC decode is ~tens of ms — far too long to
            // run on the audio thread) and return silence for now. The voice keeps its position and the
            // note becomes audible once the decode lands, clipping at most the first few ms of its attack.
            EnsureDecoding(sampleId);
            return 0;
        }

        // Offline render: no real-time deadline, so decode inline. Returning silence here would let a
        // note that out-runs the background decoder lose its attack (the per-run non-determinism that
        // shows up as "dropped notes" in a fast passage). Decode happens outside the lock; insert and
        // copy happen under it so the rented buffer can't be evicted/returned mid-copy.
        (float[] data, int length, bool pooled) = Decode(sampleId);
        lock (_cacheLock)
        {
            if (_disposed) { if (pooled) ArrayPool<float>.Shared.Return(data); return 0; }
            if (!_cache.TryGetValue(sampleId, out CacheNode? node))
            {
                if (length <= 0) return 0;   // unreadable/corrupt at play time → silence
                EvictUntilBudgetFits(length * 4L);
                node = new CacheNode { Data = data, Length = length, Pooled = pooled, LruNode = _lru.AddLast(sampleId) };
                _cache[sampleId] = node;
                _cachedBytes += length * 4L;
            }
            else if (pooled) ArrayPool<float>.Shared.Return(data);   // another reader won the insert
            return CopyFromNode(node, sampleId, frameOffset, dest, refreshLru: true);
        }
    }

    // Must hold _cacheLock. node.Length is the total float count; a frame is `channels` floats, so we
    // translate the frame-relative offset and the float-sized dest span through the channel count.
    private int CopyFromNode(CacheNode node, int sampleId, long frameOffset, Span<float> dest, bool refreshLru)
    {
        if (refreshLru && node.LruNode != null)
        {
            _lru.Remove(node.LruNode);
            node.LruNode = _lru.AddLast(sampleId);
        }
        int ch = _metadata[sampleId].Channels;
        long frameCount = node.Length / ch;
        if (frameOffset < 0 || frameOffset >= frameCount) return 0;
        var framesToCopy = (int)Math.Min(frameCount - frameOffset, dest.Length / ch);
        node.Data.AsSpan((int)(frameOffset * ch), framesToCopy * ch).CopyTo(dest);
        return framesToCopy;
    }

    // Called on the audio thread at NoteOn — start the decode early (off-thread) so the sample is
    // more likely to be ready by the time the voice reads it. In blocking mode the first ReadFrames
    // decodes synchronously, so there's nothing to pre-warm and no background task to spawn.
    public void PrepareSample(int sampleId)
    {
        if (!_blockingDecode) EnsureDecoding(sampleId);
    }

    private void EnsureDecoding(int sampleId)
    {
        lock (_cacheLock)
        {
            if (_disposed || _cache.ContainsKey(sampleId) || !_decoding.Add(sampleId)) return;
        }

        Task.Run(() =>
        {
            (float[] data, int length, bool pooled) = Decode(sampleId);   // off the audio thread
            lock (_cacheLock)
            {
                _decoding.Remove(sampleId);
                if (_disposed || _cache.ContainsKey(sampleId))
                {
                    if (pooled) ArrayPool<float>.Shared.Return(data);
                    return;
                }
                EvictUntilBudgetFits(length * 4L);
                _cache[sampleId] = new CacheNode { Data = data, Length = length, Pooled = pooled, LruNode = _lru.AddLast(sampleId) };
                _cachedBytes += length * 4L;
            }
        });
    }

    private void EvictUntilBudgetFits(long incoming)
    {
        while (_cachedBytes + incoming > _cacheBudgetBytes && _lru.Count > 0)
        {
            int victim = _lru.First!.Value;
            _lru.RemoveFirst();
            if (_cache.TryGetValue(victim, out CacheNode? v))
            {
                _cachedBytes -= v.Length * 4L;
                _cache.Remove(victim);
                if (v.Pooled) ArrayPool<float>.Shared.Return(v.Data);
            }
        }
    }

    private (float[] data, int length, bool pooled) Decode(int sampleId)
    {
        // Built-in generator placeholder ("*sine", "*silence", …): no file — emit silence sized to
        // the synthetic metadata. (Rented buffers aren't zeroed, so clear it.)
        if (_paths[sampleId].StartsWith("*", StringComparison.Ordinal))
        {
            var n = (int)Math.Min(_metadata[sampleId].LengthFrames, int.MaxValue);
            if (n <= 0) return ([], 0, false);
            float[]? silence = ArrayPool<float>.Shared.Rent(n);
            Array.Clear(silence, 0, n);
            return (silence, n, true);
        }

        DecodedAudio audio;
        try { audio = AudioCodecs.Decode(_paths[sampleId]); }
        catch { return ([], 0, false); }   // missing/corrupt at play time → silence

        var frames = (int)Math.Min(audio.FrameCount, int.MaxValue);
        if (frames <= 0) return ([], 0, false);

        int srcCh = audio.Channels;
        float[] src = audio.Samples;
        // Output channel count was decided at load from the header (1 or 2). `length` is the TOTAL
        // float count (frames × outCh) so the LRU budget — bytes = length × 4 — is correct for stereo.
        int outCh = _metadata[sampleId].Channels;

        if (outCh == 2)
        {
            // Keep L/R interleaved. Source is normally exactly stereo; if it has more channels, take
            // the first two; if it somehow decoded to mono, duplicate it to both sides.
            float[]? buf2 = ArrayPool<float>.Shared.Rent(frames * 2);
            if (srcCh == 2)
            {
                src.AsSpan(0, frames * 2).CopyTo(buf2);
            }
            else if (srcCh > 2)
            {
                for (var f = 0; f < frames; f++) { int b = f * srcCh; buf2[f * 2] = src[b]; buf2[f * 2 + 1] = src[b + 1]; }
            }
            else
            {
                for (var f = 0; f < frames; f++) { buf2[f * 2] = src[f]; buf2[f * 2 + 1] = src[f]; }
            }
            return (buf2, frames * 2, true);
        }

        // Mono target: copy a mono source straight through, or fold a multi-channel one down.
        float[]? buf = ArrayPool<float>.Shared.Rent(frames);
        if (srcCh <= 1)
        {
            src.AsSpan(0, frames).CopyTo(buf);
        }
        else
        {
            float inv = 1f / srcCh;
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f; int b = f * srcCh;
                for (var c = 0; c < srcCh; c++) sum += src[b + c];
                buf[f] = sum * inv;
            }
        }
        return (buf, frames, true);
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            if (_disposed) return;
            _disposed = true;   // set under the lock so any in-flight decode no-ops on completion
            foreach (KeyValuePair<int, CacheNode> kv in _cache)
                if (kv.Value.Pooled) ArrayPool<float>.Shared.Return(kv.Value.Data);
            _cache.Clear();
            _lru.Clear();
            _decoding.Clear();
            _cachedBytes = 0;
        }
    }
}
