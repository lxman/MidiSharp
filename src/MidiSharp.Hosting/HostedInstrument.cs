using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting;

/// <summary>
/// Hosts an instrument plugin (a sound source) — the counterpart to <see cref="HostedEffect"/>. It is
/// driven by note/parameter events rather than input audio: queue events for the block (note on/off,
/// parameter automation), then <see cref="Render"/> writes the instrument's stereo output. The output is
/// meant to feed a mixer part the same way the synth's voices do.
/// </summary>
/// <remarks>
/// Owns the wrapped <see cref="IHostedPlugin"/> and disposes it. Provides a (zeroed, never-written) input
/// bus so the adapter's process call is well-formed even though a typical instrument declares no audio
/// inputs. Blocks larger than the configured <c>MaxBlockFrames</c> are rendered in chunks, with events
/// partitioned per chunk. The realtime path allocates nothing on the managed heap.
/// </remarks>
public sealed unsafe class HostedInstrument : IDisposable
{
    private readonly IHostedPlugin _plugin;
    private readonly int _channels;
    private readonly int _maxFrames;

    private readonly UnmanagedFloatBuffer[] _inBufs;    // silent input bus (a no-input instrument ignores it)
    private readonly UnmanagedFloatBuffer[] _outBufs;
    private float** _inPtrs;
    private float** _outPtrs;

    private readonly List<HostEvent> _events = [];
    private readonly HostEvent[] _evScratch = new HostEvent[1024];
    private bool _disposed;

    public HostedInstrument(IHostedPlugin plugin, AudioConfig config)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _channels = config.ChannelCount;
        _maxFrames = config.MaxBlockFrames;

        _inBufs = new UnmanagedFloatBuffer[_channels];
        _outBufs = new UnmanagedFloatBuffer[_channels];
        for (var c = 0; c < _channels; c++)
        {
            _inBufs[c] = new UnmanagedFloatBuffer(_maxFrames);   // stays zero
            _outBufs[c] = new UnmanagedFloatBuffer(_maxFrames);
        }
        _inPtrs = (float**)NativeMemory.Alloc((nuint)_channels, (nuint)IntPtr.Size);
        _outPtrs = (float**)NativeMemory.Alloc((nuint)_channels, (nuint)IntPtr.Size);
        for (var c = 0; c < _channels; c++)
        {
            _inPtrs[c] = _inBufs[c].Pointer;
            _outPtrs[c] = _outBufs[c].Pointer;
        }
    }

    public IHostedPlugin Plugin => _plugin;

    /// <summary>Queue a MIDI note-on at <paramref name="sampleOffset"/> within the next rendered block.</summary>
    public void NoteOn(int sampleOffset, int channel, int key, int velocity)
        => _events.Add(HostEvent.Midi(sampleOffset, (byte)(0x90 | (channel & 0x0F)), (byte)(key & 0x7F), (byte)(velocity & 0x7F)));

    /// <summary>Queue a MIDI note-off at <paramref name="sampleOffset"/> within the next rendered block.</summary>
    public void NoteOff(int sampleOffset, int channel, int key)
        => _events.Add(HostEvent.Midi(sampleOffset, (byte)(0x80 | (channel & 0x0F)), (byte)(key & 0x7F), 0));

    /// <summary>Queue any timed event (CC, parameter automation, …) for the next block.</summary>
    public void QueueEvent(HostEvent e) => _events.Add(e);

    /// <summary>
    /// Render one block: writes the instrument's stereo output into <paramref name="interleavedStereo"/>
    /// (overwriting it), driven by the events queued since the last render. Length is <c>frames × 2</c>.
    /// </summary>
    public void Render(Span<float> interleavedStereo)
    {
        if (_disposed || _channels != 2) return;

        int total = interleavedStereo.Length / 2;
        bool hasEvents = _events.Count > 0;
        var done = 0;
        while (done < total)
        {
            int n = Math.Min(_maxFrames, total - done);
            var input = new PlanarBuffers(_inPtrs, _channels, n);     // silent
            var output = new PlanarBuffers(_outPtrs, _channels, n);
            _plugin.Process(input, output, hasEvents ? ChunkEvents(done, done + n) : ReadOnlySpan<HostEvent>.Empty);
            PlanarBridge.InterleaveStereo(_outBufs[0].Span[..n], _outBufs[1].Span[..n], interleavedStereo.Slice(done * 2, n * 2));
            done += n;
        }
        if (hasEvents) _events.Clear();
    }

    private ReadOnlySpan<HostEvent> ChunkEvents(int start, int end)
    {
        Span<HostEvent> src = CollectionsMarshal.AsSpan(_events);
        var m = 0;
        for (var i = 0; i < src.Length && m < _evScratch.Length; i++)
        {
            int off = src[i].SampleOffset;
            if (off >= start && off < end) _evScratch[m++] = src[i] with { SampleOffset = off - start };
        }
        return _evScratch.AsSpan(0, m);
    }

    /// <summary>Clear queued events (e.g. on transport stop). Plugin-internal voice state is the plugin's own.</summary>
    public void Reset() => _events.Clear();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_inPtrs != null) { NativeMemory.Free(_inPtrs); _inPtrs = null; }
        if (_outPtrs != null) { NativeMemory.Free(_outPtrs); _outPtrs = null; }
        for (var c = 0; c < _channels; c++) { _inBufs[c]?.Dispose(); _outBufs[c]?.Dispose(); }
        _plugin.Dispose();
    }
}
