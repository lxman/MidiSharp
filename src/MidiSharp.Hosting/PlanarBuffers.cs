using System;

namespace MidiSharp.Hosting;

/// <summary>
/// A realtime, non-owning view over planar (non-interleaved) channel buffers — one contiguous float
/// buffer per channel, which is what every plugin format speaks (LADSPA per-port, VST2/VST3
/// <c>float**</c>, CLAP <c>clap_audio_buffer.data32</c>). The backing memory is unmanaged and owned by
/// the caller (a <see cref="HostedEffect"/>'s pre-allocated scratch); this struct just carries the
/// pointer-of-pointers so a format adapter can hand it straight to native code with no marshaling on
/// the audio thread.
/// </summary>
public readonly unsafe struct PlanarBuffers
{
    private readonly float** _channels;

    public PlanarBuffers(float** channels, int channelCount, int frames)
    {
        _channels = channels;
        ChannelCount = channelCount;
        Frames = frames;
    }

    public int ChannelCount { get; }
    public int Frames { get; }

    /// <summary>The raw <c>float**</c> a native process call expects.</summary>
    public float** Channels => _channels;

    /// <summary>Pointer to one channel's contiguous samples.</summary>
    public float* Channel(int index) => _channels[index];

    /// <summary>One channel as a managed span (for managed processors / tests). Length = <see cref="Frames"/>.</summary>
    public Span<float> ChannelSpan(int index) => new(_channels[index], Frames);
}
