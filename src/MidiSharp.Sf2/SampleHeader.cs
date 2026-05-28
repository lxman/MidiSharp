namespace MidiSharp.Sf2;

/// <summary>
/// Represents an SF2 sample header with metadata about an audio sample.
/// </summary>
public sealed class SampleHeader
{
    /// <summary>
    /// Sample name (up to 20 characters).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Start offset in sample data (in sample points).
    /// </summary>
    public uint Start { get; set; }

    /// <summary>
    /// End offset in sample data (in sample points).
    /// </summary>
    public uint End { get; set; }

    /// <summary>
    /// Loop start offset (in sample points).
    /// </summary>
    public uint StartLoop { get; set; }

    /// <summary>
    /// Loop end offset (in sample points).
    /// </summary>
    public uint EndLoop { get; set; }

    /// <summary>
    /// Sample rate in Hz.
    /// </summary>
    public uint SampleRate { get; set; }

    /// <summary>
    /// Original MIDI pitch (60 = middle C).
    /// </summary>
    public byte OriginalPitch { get; set; }

    /// <summary>
    /// Pitch correction in cents (-50 to +50).
    /// </summary>
    public sbyte PitchCorrection { get; set; }

    /// <summary>
    /// Link to stereo pair sample.
    /// </summary>
    public ushort SampleLinkIndex { get; set; }

    /// <summary>
    /// Sample type (mono, stereo, ROM, etc.).
    /// </summary>
    public SampleLink SampleType { get; set; }

    /// <summary>
    /// Index of this sample in the sample list.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Length of the sample in sample points.
    /// </summary>
    public uint Length => End - Start;

    /// <summary>
    /// Duration of the sample in seconds.
    /// </summary>
    public double Duration => SampleRate > 0 ? (double)Length / SampleRate : 0;

    /// <summary>
    /// Whether this is a ROM sample.
    /// </summary>
    public bool IsRomSample => ((ushort)SampleType & 0x8000) != 0;

    /// <summary>
    /// Whether this is a mono sample.
    /// </summary>
    public bool IsMono => (SampleType & SampleLink.MonoSample) != 0;

    /// <summary>
    /// Whether this is a left channel sample.
    /// </summary>
    public bool IsLeft => (SampleType & SampleLink.LeftSample) != 0;

    /// <summary>
    /// Whether this is a right channel sample.
    /// </summary>
    public bool IsRight => (SampleType & SampleLink.RightSample) != 0;

    /// <summary>
    /// Whether this sample has a stereo link.
    /// </summary>
    public bool IsLinked => (SampleType & SampleLink.LinkedSample) != 0;

    public SampleHeader()
    {
    }

    public SampleHeader(RawSampleHeader raw, int index)
    {
        Name = raw.GetName();
        Start = raw.Start;
        End = raw.End;
        StartLoop = raw.StartLoop;
        EndLoop = raw.EndLoop;
        SampleRate = raw.SampleRate;
        OriginalPitch = raw.OriginalPitch;
        PitchCorrection = raw.PitchCorrection;
        SampleLinkIndex = raw.SampleLink;
        SampleType = (SampleLink)raw.SampleType;
        Index = index;
    }

    /// <summary>
    /// Converts to raw format for writing.
    /// </summary>
    public RawSampleHeader ToRaw()
    {
        var raw = new RawSampleHeader
        {
            Start = Start,
            End = End,
            StartLoop = StartLoop,
            EndLoop = EndLoop,
            SampleRate = SampleRate,
            OriginalPitch = OriginalPitch,
            PitchCorrection = PitchCorrection,
            SampleLink = SampleLinkIndex,
            SampleType = (ushort)SampleType
        };
        raw.SetName(Name);
        return raw;
    }

    public override string ToString()
    {
        return $"{Name} ({SampleRate}Hz, {Length} samples, {Duration:F2}s)";
    }
}

/// <summary>
/// Represents a sample with both header and audio data.
/// </summary>
public sealed class Sample
{
    /// <summary>
    /// The sample header with metadata.
    /// </summary>
    public SampleHeader Header { get; set; }

    /// <summary>
    /// The audio data as 16-bit PCM samples.
    /// </summary>
    public short[]? Data { get; set; }

    public Sample()
    {
        Header = new SampleHeader();
    }

    public Sample(SampleHeader header)
    {
        Header = header;
    }

    public Sample(SampleHeader header, short[] data)
    {
        Header = header;
        Data = data;
    }

    public override string ToString() => Header.ToString();
}
