namespace DLS.Net;

/// <summary>
/// DLS connection-block source codes (DLS Level 2 §1.7.1, "Source Enumerations").
/// These identify the modulation source side of an articulator connection.
/// </summary>
public enum ConnectionSource : ushort
{
    None = 0x0000,
    Lfo = 0x0001,
    KeyOnVelocity = 0x0002,
    KeyNumber = 0x0003,
    Eg1 = 0x0004,
    Eg2 = 0x0005,
    PitchWheel = 0x0006,
    PolyPressure = 0x0007,
    ChannelPressure = 0x0008,
    Vibrato = 0x0009,

    // MIDI controllers (DLS uses CC numbers offset by 0x80)
    Cc1 = 0x0081,    // Modulation
    Cc7 = 0x0087,    // Volume
    Cc10 = 0x008A,   // Pan
    Cc11 = 0x008B,   // Expression
    Cc91 = 0x00DB,   // Reverb send
    Cc93 = 0x00DD,   // Chorus send

    RpnPitchBendRange = 0x0100,
    RpnFineTune = 0x0101,
    RpnCoarseTune = 0x0102,
}

/// <summary>
/// DLS connection-block destination codes. These identify the modulation
/// destination — what the connection's scaled source value adjusts.
/// </summary>
public enum ConnectionDestination : ushort
{
    None = 0x0000,
    Gain = 0x0001,        // attenuation
    Reserved = 0x0002,
    Pitch = 0x0003,
    Pan = 0x0004,
    KeyNumber = 0x0005,

    LeftEg = 0x0010,
    RightEg = 0x0011,
    LeftReverb = 0x0012,
    RightReverb = 0x0013,
    LeftChorus = 0x0014,
    RightChorus = 0x0015,

    LfoFrequency = 0x0104,
    LfoStartDelay = 0x0105,
    VibratoFrequency = 0x0114,
    VibratoStartDelay = 0x0115,

    Eg1AttackTime = 0x0206,
    Eg1DecayTime = 0x0207,
    Eg1ReleaseTime = 0x0209,
    Eg1SustainLevel = 0x020A,
    Eg1DelayTime = 0x020B,
    Eg1HoldTime = 0x020C,

    Eg2AttackTime = 0x030A,
    Eg2DecayTime = 0x030B,
    Eg2ReleaseTime = 0x030D,
    Eg2SustainLevel = 0x030E,
    Eg2DelayTime = 0x030F,
    Eg2HoldTime = 0x0310,

    FilterCutoff = 0x0500,
    FilterQ = 0x0501,
}

/// <summary>
/// DLS connection-block transform codes. The low 4 bits select a curve
/// (linear/concave/convex/switch); higher bits encode polarity / direction —
/// the SF2 modulator source format with a different layout.
/// </summary>
public enum ConnectionTransform : ushort
{
    Linear = 0x0000,
    Concave = 0x0001,
    Convex = 0x0002,
    Switch = 0x0003,
}

/// <summary>
/// Loop type stored in each WLOOP record inside a wsmp chunk. DLS Level 2
/// §1.14.2 defines exactly two values; the absence of a loop record means
/// "no loop" (don't look up a separate sentinel).
/// </summary>
public enum DlsLoopType : uint
{
    Forward = 0,    // WLOOP_TYPE_FORWARD
    Release = 1,    // WLOOP_TYPE_RELEASE
}

/// <summary>
/// WAVE format tag from the embedded fmt chunk.
/// </summary>
public enum WaveFormatTag : ushort
{
    Pcm = 0x0001,
    IeeeFloat = 0x0003,
    Extensible = 0xFFFE,
}
