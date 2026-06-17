using System;
using System.Collections.Generic;

namespace Loader.Dls;

/// <summary>
/// One DLS articulator connection — modulation source × control × destination
/// with transform and scale. Maps directly to an SF2-style modulator and (later)
/// to the IR's <c>ModulationRoute</c>.
/// </summary>
public readonly struct ConnectionBlock
{
    public readonly ConnectionSource Source;
    public readonly ushort Control;
    public readonly ConnectionDestination Destination;
    /// <summary>
    /// Raw 16-bit transform word. Decode via:
    /// bits 0-3 = source curve (<see cref="ConnectionTransform"/>),
    /// bit 4 = source polarity (1 = bipolar),
    /// bit 5 = source direction (1 = inverted, i.e. high source → low output),
    /// bits 8-15 = destination-side curve/polarity/direction (unused by the IR mapping today).
    /// </summary>
    public readonly ushort Transform;
    public readonly int Scale;

    public ConnectionBlock(ConnectionSource src, ushort ctrl, ConnectionDestination dst, ushort xform, int scale)
    {
        Source = src; Control = ctrl; Destination = dst; Transform = xform; Scale = scale;
    }

    public ConnectionTransform SourceCurve => (ConnectionTransform)(Transform & 0x000F);
    public bool SourceBipolar => (Transform & 0x0010) != 0;
    public bool SourceInverted => (Transform & 0x0020) != 0;
}

/// <summary>
/// One sample loop point inside a wsmp chunk. DLS allows multiple loops per
/// sample, though the common case is exactly one.
/// </summary>
public readonly struct SampleLoop
{
    public readonly DlsLoopType LoopType;
    public readonly uint StartFrame;
    public readonly uint LengthFrames;

    public SampleLoop(DlsLoopType type, uint start, uint length)
    {
        LoopType = type; StartFrame = start; LengthFrames = length;
    }
}

/// <summary>
/// Wave sample table info (wsmp chunk). Sits either on a region (overriding
/// the wave's defaults) or on the wave itself (defaults).
/// </summary>
public sealed class WaveSampleInfo
{
    public byte UnityNote { get; init; }              // root key
    public short FineTuneCents { get; init; }         // fixed-point relative pitch
    public int GainCentibels { get; init; }           // attenuation (DLS stores as gain — typically ≤ 0)
    public uint Options { get; init; }
    public IReadOnlyList<SampleLoop> Loops { get; init; } = Array.Empty<SampleLoop>();
}

/// <summary>
/// One wave entry in the wave pool — the PCM data plus its format metadata.
/// </summary>
public sealed class DlsWave
{
    public int Index { get; init; }
    public WaveFormatTag FormatTag { get; init; }
    public ushort Channels { get; init; }
    public uint SampleRate { get; init; }
    public ushort BitsPerSample { get; init; }
    public ushort BlockAlign { get; init; }
    public string? Name { get; init; }

    /// <summary>Default wsmp from inside the wave block (optional).</summary>
    public WaveSampleInfo? SampleInfo { get; init; }

    /// <summary>The data chunk's raw PCM bytes. Frame layout depends on FormatTag/BitsPerSample.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>
/// Wave-link record (wlnk chunk) — points a region to a specific wave in the
/// pool.
/// </summary>
public readonly struct WaveLink
{
    public readonly ushort Options;
    public readonly ushort PhaseGroup;
    public readonly uint Channel;
    public readonly uint TableIndex;
    public WaveLink(ushort opt, ushort phase, uint ch, uint idx)
    {
        Options = opt; PhaseGroup = phase; Channel = ch; TableIndex = idx;
    }
}

/// <summary>
/// One playable region inside an instrument (rgn or rgn2 chunk).
/// </summary>
public sealed class DlsRegion
{
    public byte KeyLow { get; init; }
    public byte KeyHigh { get; init; }
    public byte VelocityLow { get; init; }
    public byte VelocityHigh { get; init; }
    public ushort Options { get; init; }
    public ushort KeyGroup { get; init; }              // exclusive class
    public WaveLink WaveLink { get; init; }
    public WaveSampleInfo? SampleInfo { get; init; }
    public IReadOnlyList<ArticulatorList> Articulators { get; init; } = Array.Empty<ArticulatorList>();
}

/// <summary>
/// A single articulator block (art1 / art2). Each contains a set of
/// connection blocks. Multiple ArticulatorList instances on a region or
/// instrument compose — later blocks override matching connections per
/// DLS Level 2 §1.7.
/// </summary>
public sealed class ArticulatorList
{
    public bool IsLevel2 { get; init; }                // art1 (Level 1) vs art2 (Level 2)
    public IReadOnlyList<ConnectionBlock> Connections { get; init; } = Array.Empty<ConnectionBlock>();
}

/// <summary>
/// One DLS instrument — addressable by (bank, program) like an SF2 preset.
/// </summary>
public sealed class DlsInstrument
{
    public uint Bank { get; init; }
    public uint Program { get; init; }
    public bool IsDrumKit { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<DlsRegion> Regions { get; init; } = Array.Empty<DlsRegion>();
    public IReadOnlyList<ArticulatorList> Articulators { get; init; } = Array.Empty<ArticulatorList>();
}

/// <summary>
/// A loaded DLS file's contents. Top-level collection of instruments plus a
/// shared wave pool that regions index into.
/// </summary>
public sealed class DlsCollection
{
    public string? Name { get; init; }
    public string? Copyright { get; init; }
    public string? Engineer { get; init; }
    public string? Comments { get; init; }
    public IReadOnlyList<DlsInstrument> Instruments { get; init; } = Array.Empty<DlsInstrument>();
    public IReadOnlyList<DlsWave> Waves { get; init; } = Array.Empty<DlsWave>();
}
