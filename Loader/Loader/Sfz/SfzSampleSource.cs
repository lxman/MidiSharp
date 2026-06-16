using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using MidiSharp.Audio;

namespace MidiSharp.SoundBank.Sfz;

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

    private readonly object _cacheLock = new();
    private readonly Dictionary<int, CacheNode> _cache = new();
    private readonly LinkedList<int> _lru = new();
    private readonly HashSet<int> _decoding = new();   // samples a background decode is in flight for
    private long _cachedBytes;
    private bool _disposed;

    private sealed class CacheNode
    {
        public float[] Data = Array.Empty<float>();
        public int Length;
        public bool Pooled;
        public LinkedListNode<int>? LruNode;
    }

    public int Count => _metadata.Length;
    public SampleMetadata Metadata(int sampleId) => _metadata[sampleId];

    public SfzSampleSource(IReadOnlyList<string> paths, IReadOnlyList<SampleMetadata> metadata, long cacheBudgetBytes)
    {
        if (paths.Count != metadata.Count)
            throw new ArgumentException("paths and metadata must have the same count");
        _paths = new string[paths.Count];
        _metadata = new SampleMetadata[metadata.Count];
        for (int i = 0; i < paths.Count; i++) { _paths[i] = paths[i]; _metadata[i] = metadata[i]; }
        // Floor the budget so the active working set (many sustained piano voices, each a multi-MB
        // decoded sample) fits without the cache thrashing decode→evict→re-decode under polyphony.
        const long MinBudget = 256L * 1024 * 1024;
        _cacheBudgetBytes = cacheBudgetBytes <= 0 ? long.MaxValue : Math.Max(cacheBudgetBytes, MinBudget);
    }

    public int ReadFrames(int sampleId, long frameOffset, Span<float> dest)
    {
        lock (_cacheLock)
            if (_cache.TryGetValue(sampleId, out var hit))
                return CopyFromNode(hit, sampleId, frameOffset, dest, refreshLru: true);

        // Not decoded yet: kick off a background decode (FLAC decode is ~tens of ms — far too long to
        // run on the audio thread) and return silence for now. The voice keeps its position and the
        // note becomes audible once the decode lands, clipping at most the first few ms of its attack.
        EnsureDecoding(sampleId);
        return 0;
    }

    // Must hold _cacheLock. SFZ samples are folded to mono, so a frame is one float.
    private int CopyFromNode(CacheNode node, int sampleId, long frameOffset, Span<float> dest, bool refreshLru)
    {
        if (refreshLru && node.LruNode != null)
        {
            _lru.Remove(node.LruNode);
            node.LruNode = _lru.AddLast(sampleId);
        }
        if (frameOffset < 0 || frameOffset >= node.Length) return 0;
        int n = (int)Math.Min(node.Length - frameOffset, dest.Length);
        node.Data.AsSpan((int)frameOffset, n).CopyTo(dest);
        return n;
    }

    // Called on the audio thread at NoteOn — start the decode early (off-thread) so the sample is
    // more likely to be ready by the time the voice reads it.
    public void PrepareSample(int sampleId) => EnsureDecoding(sampleId);

    private void EnsureDecoding(int sampleId)
    {
        lock (_cacheLock)
        {
            if (_disposed || _cache.ContainsKey(sampleId) || !_decoding.Add(sampleId)) return;
        }

        Task.Run(() =>
        {
            var (data, length, pooled) = Decode(sampleId);   // off the audio thread
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
            if (_cache.TryGetValue(victim, out var v))
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
            int n = (int)Math.Min(_metadata[sampleId].LengthFrames, int.MaxValue);
            if (n <= 0) return (Array.Empty<float>(), 0, false);
            var silence = ArrayPool<float>.Shared.Rent(n);
            Array.Clear(silence, 0, n);
            return (silence, n, true);
        }

        DecodedAudio audio;
        try { audio = AudioCodecs.Decode(_paths[sampleId]); }
        catch { return (Array.Empty<float>(), 0, false); }   // missing/corrupt at play time → silence

        int frames = (int)Math.Min(audio.FrameCount, int.MaxValue);
        if (frames <= 0) return (Array.Empty<float>(), 0, false);

        var buf = ArrayPool<float>.Shared.Rent(frames);
        if (audio.Channels <= 1)
        {
            audio.Samples.AsSpan(0, frames).CopyTo(buf);
        }
        else
        {
            int ch = audio.Channels;
            float inv = 1f / ch;
            var src = audio.Samples;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f; int b = f * ch;
                for (int c = 0; c < ch; c++) sum += src[b + c];
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
            foreach (var kv in _cache)
                if (kv.Value.Pooled) ArrayPool<float>.Shared.Return(kv.Value.Data);
            _cache.Clear();
            _lru.Clear();
            _decoding.Clear();
            _cachedBytes = 0;
        }
    }
}
