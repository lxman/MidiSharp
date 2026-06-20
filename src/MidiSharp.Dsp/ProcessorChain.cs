using System;
using System.Collections.Generic;

namespace MidiSharp.Dsp;

/// <summary>
/// An ordered chain of <see cref="IAudioProcessor"/>s that is itself a processor: it runs each member
/// in turn over the block. This is the master insert chain the host inserts at the audio-callback seam
/// (and, later, a per-instrument insert chain).
/// </summary>
/// <remarks>
/// Mutations (<see cref="Add"/>/<see cref="Remove"/>/<see cref="Clear"/>) publish a fresh immutable
/// snapshot under a lock; <see cref="Process"/> reads the snapshot without locking, so the audio
/// thread never blocks and never sees a half-edited list. A processor added mid-stream takes effect on
/// the next block.
/// </remarks>
public sealed class ProcessorChain : IAudioProcessor
{
    private readonly object _gate = new();
    private readonly List<IAudioProcessor> _items = [];
    private volatile IAudioProcessor[] _snapshot = [];

    /// <summary>When true, <see cref="Process"/> leaves the block untouched (the whole chain is bypassed).</summary>
    public bool Bypass { get; set; }

    /// <summary>The current processors, in order. A point-in-time snapshot; safe to enumerate.</summary>
    public IReadOnlyList<IAudioProcessor> Processors => _snapshot;

    public void Add(IAudioProcessor processor)
    {
        if (processor == null) throw new ArgumentNullException(nameof(processor));
        lock (_gate)
        {
            _items.Add(processor);
            _snapshot = _items.ToArray();
        }
    }

    public bool Remove(IAudioProcessor processor)
    {
        lock (_gate)
        {
            var removed = _items.Remove(processor);
            if (removed) _snapshot = _items.ToArray();
            return removed;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            _snapshot = [];
        }
    }

    /// <summary>
    /// Atomically replaces the whole chain with <paramref name="processors"/>, in order. Publishes one
    /// new snapshot, so the audio thread never sees a half-rebuilt chain (used for rack reorder/add/remove).
    /// </summary>
    public void SetAll(IReadOnlyList<IAudioProcessor> processors)
    {
        if (processors == null) throw new ArgumentNullException(nameof(processors));
        lock (_gate)
        {
            _items.Clear();
            for (var i = 0; i < processors.Count; i++)
            {
                if (processors[i] == null) throw new ArgumentException("null processor", nameof(processors));
                _items.Add(processors[i]);
            }
            _snapshot = _items.ToArray();
        }
    }

    public void Process(Span<float> interleavedStereo)
    {
        if (Bypass) return;
        var chain = _snapshot;            // single volatile read; stable for this block
        for (var i = 0; i < chain.Length; i++)
            chain[i].Process(interleavedStereo);
    }

    public void Reset()
    {
        var chain = _snapshot;
        for (var i = 0; i < chain.Length; i++)
            chain[i].Reset();
    }
}
