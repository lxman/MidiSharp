using System;
using System.Collections.Generic;

namespace MidiSharp.Hosting;

/// <summary>
/// One loaded plugin instance, format-agnostic. A format adapter (LADSPA/CLAP/VST2/VST3) implements
/// this over its native plugin; the host drives it through this interface alone.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="Activate"/> (allocate realtime buffers for a given sample rate / max block) →
/// repeated <see cref="Process"/> on the audio thread → <see cref="Deactivate"/> → <see cref="IDisposable.Dispose"/>.
/// <see cref="Process"/> is the realtime hot path: it must not allocate on the managed heap or take a
/// lock. Parameter writes and state load happen off the audio thread.
/// </remarks>
public interface IHostedPlugin : IDisposable
{
    PluginDescriptor Descriptor { get; }

    /// <summary>True for sound sources (synths) that render from <see cref="HostEvent"/>s; false for effects.</summary>
    bool IsInstrument { get; }

    /// <summary>The plugin's automatable parameters (normalized 0..1 via each <see cref="PluginParameter"/>).</summary>
    IReadOnlyList<PluginParameter> Parameters { get; }

    /// <summary>Prepare for playback at a given rate/block size; allocate realtime buffers. Off the audio thread.</summary>
    void Activate(AudioConfig config);

    /// <summary>Stop playback; release realtime buffers. Off the audio thread.</summary>
    void Deactivate();

    /// <summary>
    /// Realtime: render one block. For an effect, <paramref name="input"/> holds the incoming audio and
    /// the result is written to <paramref name="output"/> (in-place is allowed when the adapter aliases
    /// them). For an instrument, <paramref name="input"/> may be empty and <paramref name="events"/>
    /// drives the sound. <paramref name="events"/> are sample-accurate within the block. Must not
    /// allocate or lock.
    /// </summary>
    void Process(PlanarBuffers input, PlanarBuffers output, ReadOnlySpan<HostEvent> events);

    /// <summary>Current value of a parameter, normalized 0..1.</summary>
    double GetParameter(int index);

    /// <summary>Set a parameter from a normalized 0..1 value. Realtime-safe (scalar write / queued event).</summary>
    void SetParameter(int index, double normalized);

    /// <summary>Opaque plugin state for persistence (base64'd into the Setup JSON). Empty when unsupported.</summary>
    byte[] SaveState();

    /// <summary>Restore opaque state previously returned by <see cref="SaveState"/>. Off the audio thread.</summary>
    void LoadState(ReadOnlySpan<byte> state);

    /// <summary>The plugin's native editor, or null when it has none. Drives the editor-window lifecycle
    /// (see <see cref="IPluginGui"/>); independent of audio. Default null — adapters opt in.</summary>
    IPluginGui? Gui => null;
}
