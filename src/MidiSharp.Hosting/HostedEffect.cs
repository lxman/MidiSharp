using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MidiSharp.Dsp;

namespace MidiSharp.Hosting;

/// <summary>
/// Adapts a hosted effect plugin to <see cref="IAudioProcessor"/> so it drops straight into a
/// <c>MidiSharp.Dsp.ProcessorChain</c> (and thus the master bus or any per-instrument insert rack) with
/// no engine change. It owns the realtime planar scratch buffers and runs the
/// interleaved↔planar bridge around the plugin's process call.
/// </summary>
/// <remarks>
/// Owns the wrapped <see cref="IHostedPlugin"/> and disposes it. The plugin must be activated against a
/// matching <see cref="AudioConfig"/> before <see cref="Process"/> is called. Blocks larger than the
/// configured <c>MaxBlockFrames</c> are processed in chunks, so any callback size is safe. The realtime
/// path allocates nothing on the managed heap.
/// </remarks>
public sealed unsafe class HostedEffect : IAudioProcessor, IDisposable
{
    private readonly IHostedPlugin _plugin;
    private readonly int _channels;
    private readonly int _maxFrames;

    private readonly UnmanagedFloatBuffer[] _inBufs;
    private readonly UnmanagedFloatBuffer[] _outBufs;
    private float** _inPtrs;
    private float** _outPtrs;

    // Per-block timed events queued before Process (param automation / MIDI). Filled off the audio
    // thread; drained and cleared in Process. Empty by default, so the no-event path stays alloc-free.
    private readonly List<HostEvent> _events = [];
    private readonly HostEvent[] _evScratch = new HostEvent[512];
    private bool _disposed;

    /// <summary>
    /// Wrap an already-activated effect plugin. <paramref name="config"/> must match the config the
    /// plugin was activated against (same channel count and max block size).
    /// </summary>
    public HostedEffect(IHostedPlugin plugin, AudioConfig config)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _channels = config.ChannelCount;
        _maxFrames = config.MaxBlockFrames;

        _inBufs = new UnmanagedFloatBuffer[_channels];
        _outBufs = new UnmanagedFloatBuffer[_channels];
        for (var c = 0; c < _channels; c++)
        {
            _inBufs[c] = new UnmanagedFloatBuffer(_maxFrames);
            _outBufs[c] = new UnmanagedFloatBuffer(_maxFrames);
        }

        // Unmanaged pointer-of-pointers the plugin's native process call consumes, filled once.
        _inPtrs = (float**)NativeMemory.Alloc((nuint)_channels, (nuint)IntPtr.Size);
        _outPtrs = (float**)NativeMemory.Alloc((nuint)_channels, (nuint)IntPtr.Size);
        for (var c = 0; c < _channels; c++)
        {
            _inPtrs[c] = _inBufs[c].Pointer;
            _outPtrs[c] = _outBufs[c].Pointer;
        }
    }

    /// <summary>The wrapped plugin, e.g. to read its parameters for the UI.</summary>
    public IHostedPlugin Plugin => _plugin;

    /// <summary>
    /// Queue a timed event (parameter automation or MIDI) for the next <see cref="Process"/>. Its
    /// <see cref="HostEvent.SampleOffset"/> is relative to the start of that block. Call before the block,
    /// off the audio thread; events should be queued in ascending sample order.
    /// </summary>
    public void QueueEvent(HostEvent e) => _events.Add(e);

    public void Process(Span<float> interleavedStereo)
    {
        if (_disposed || _channels != 2) return;   // stereo bus for now (the engine's contract)

        var total = interleavedStereo.Length / 2;
        var hasEvents = _events.Count > 0;
        var done = 0;
        while (done < total)
        {
            var n = Math.Min(_maxFrames, total - done);
            var slice = interleavedStereo.Slice(done * 2, n * 2);

            PlanarBridge.DeinterleaveStereo(slice, _inBufs[0].Span[..n], _inBufs[1].Span[..n]);

            var input = new PlanarBuffers(_inPtrs, _channels, n);
            var output = new PlanarBuffers(_outPtrs, _channels, n);
            _plugin.Process(input, output, hasEvents ? ChunkEvents(done, done + n) : ReadOnlySpan<HostEvent>.Empty);

            PlanarBridge.InterleaveStereo(_outBufs[0].Span[..n], _outBufs[1].Span[..n], slice);
            done += n;
        }
        if (hasEvents) _events.Clear();
    }

    // Events whose offset falls in [start, end) of the full block, rebased to the chunk's local offset.
    // For the common single-chunk block this is the whole queue with offsets unchanged.
    private ReadOnlySpan<HostEvent> ChunkEvents(int start, int end)
    {
        var src = CollectionsMarshal.AsSpan(_events);
        var m = 0;
        for (var i = 0; i < src.Length && m < _evScratch.Length; i++)
        {
            var off = src[i].SampleOffset;
            if (off >= start && off < end) _evScratch[m++] = src[i] with { SampleOffset = off - start };
        }
        return _evScratch.AsSpan(0, m);
    }

    /// <summary>
    /// Clears the host's planar scratch. Deep plugin-state reset (delay lines etc.) is format-specific
    /// and handled by the adapter; this keeps the bridge buffers clean across a transport jump.
    /// </summary>
    public void Reset()
    {
        for (var c = 0; c < _channels; c++)
        {
            _inBufs[c].Clear();
            _outBufs[c].Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_inPtrs != null) { NativeMemory.Free(_inPtrs); _inPtrs = null; }
        if (_outPtrs != null) { NativeMemory.Free(_outPtrs); _outPtrs = null; }
        for (var c = 0; c < _channels; c++)
        {
            _inBufs[c]?.Dispose();
            _outBufs[c]?.Dispose();
        }
        _plugin.Dispose();
    }
}
