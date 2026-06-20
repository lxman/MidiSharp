namespace MidiSharp.Hosting;

/// <summary>What a <see cref="HostEvent"/> carries.</summary>
public enum HostEventKind : byte
{
    /// <summary>A raw MIDI message (<see cref="HostEvent.Status"/>/<see cref="HostEvent.Data1"/>/<see cref="HostEvent.Data2"/>).</summary>
    Midi,
    /// <summary>A parameter change (<see cref="HostEvent.ParamIndex"/> → <see cref="HostEvent.ParamValue"/>, normalized 0..1).</summary>
    Param,
}

/// <summary>
/// A format-neutral, sample-accurate event handed to a plugin for the current block — the union of a
/// MIDI message and a parameter change. <see cref="SampleOffset"/> is the position within the block at
/// which the event takes effect, so MIDI notes and parameter automation stay sample-accurate rather than
/// quantized to the block boundary. Adapters translate it to the format's native event
/// (CLAP <c>clap_event_*</c>, VST <c>VstMidiEvent</c>/param queues).
/// </summary>
public readonly record struct HostEvent
{
    /// <summary>Frames from the start of the current block (0..blockFrames-1) at which this event applies.</summary>
    public int SampleOffset { get; init; }

    public HostEventKind Kind { get; init; }

    // ── MIDI (Kind == Midi) ──
    public byte Status { get; init; }
    public byte Data1 { get; init; }
    public byte Data2 { get; init; }

    // ── Parameter (Kind == Param) ──
    public int ParamIndex { get; init; }
    public double ParamValue { get; init; }   // normalized 0..1

    /// <summary>A raw MIDI message at <paramref name="sampleOffset"/>.</summary>
    public static HostEvent Midi(int sampleOffset, byte status, byte data1, byte data2) => new()
    {
        SampleOffset = sampleOffset,
        Kind = HostEventKind.Midi,
        Status = status,
        Data1 = data1,
        Data2 = data2,
    };

    /// <summary>A normalized (0..1) parameter change at <paramref name="sampleOffset"/>.</summary>
    public static HostEvent Param(int sampleOffset, int paramIndex, double normalized) => new()
    {
        SampleOffset = sampleOffset,
        Kind = HostEventKind.Param,
        ParamIndex = paramIndex,
        ParamValue = normalized,
    };
}
