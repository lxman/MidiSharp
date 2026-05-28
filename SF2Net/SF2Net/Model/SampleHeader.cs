namespace SF2Net;

/// <summary>
/// A SHDR record. <see cref="Name"/> is decoded from the 20-byte ASCII field; offsets are in sample frames within the smpl chunk.
/// </summary>
public sealed class SampleHeader
{
    public string Name { get; set; } = string.Empty;
    public uint Start { get; set; }
    public uint End { get; set; }
    public uint StartLoop { get; set; }
    public uint EndLoop { get; set; }
    public uint SampleRate { get; set; }
    public byte OriginalPitch { get; set; }
    public sbyte PitchCorrection { get; set; }
    public ushort SampleLink { get; set; }
    public SFSampleLink SampleType { get; set; }

    /// <summary>Index assigned by the loader after EOS removal — used internally for relinking on extract.</summary>
    internal uint Index { get; set; }
    internal uint OriginalIndex { get; set; }
    /// <summary>Computed: the start of the next sample (or end of smpl chunk) — used to bound the writable span.</summary>
    internal uint EndOfRegion { get; set; }

    public uint LengthFrames => End - Start;

    public override string ToString() => $"{Name} ({SampleType}, {SampleRate}Hz, {LengthFrames} frames)";
}

/// <summary>
/// Public projection of a <see cref="SampleHeader"/> with offsets rebased to zero.
/// Equivalent of the C++ <c>sfSampleItem</c>.
/// </summary>
public sealed class SampleInfo
{
    public string Name { get; init; } = string.Empty;
    public uint Start { get; init; }
    public uint End { get; init; }
    public uint StartLoop { get; init; }
    public uint EndLoop { get; init; }
    public uint SampleRate { get; init; }
    public byte OriginalPitch { get; init; }
    public sbyte PitchCorrection { get; init; }
    public ushort SampleLink { get; init; }
    public SFSampleLink SampleType { get; init; }

    public uint LengthFrames => End - Start;
}
