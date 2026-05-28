using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MidiSharp.Sf2;

/// <summary>
/// SF2 Generator types - parameters that control sound generation.
/// </summary>
public enum GeneratorType : ushort
{
    StartAddrsOffset = 0,
    EndAddrsOffset = 1,
    StartLoopAddrsOffset = 2,
    EndLoopAddrsOffset = 3,
    StartAddrsCoarseOffset = 4,
    ModLfoToPitch = 5,
    VibLfoToPitch = 6,
    ModEnvToPitch = 7,
    InitialFilterFc = 8,
    InitialFilterQ = 9,
    ModLfoToFilterFc = 10,
    ModEnvToFilterFc = 11,
    EndAddrsCoarseOffset = 12,
    ModLfoToVolume = 13,
    // 14 unused
    ChorusEffectsSend = 15,
    ReverbEffectsSend = 16,
    Pan = 17,
    // 18-20 unused
    DelayModLfo = 21,
    FreqModLfo = 22,
    DelayVibLfo = 23,
    FreqVibLfo = 24,
    DelayModEnv = 25,
    AttackModEnv = 26,
    HoldModEnv = 27,
    DecayModEnv = 28,
    SustainModEnv = 29,
    ReleaseModEnv = 30,
    KeynumToModEnvHold = 31,
    KeynumToModEnvDecay = 32,
    DelayVolEnv = 33,
    AttackVolEnv = 34,
    HoldVolEnv = 35,
    DecayVolEnv = 36,
    SustainVolEnv = 37,
    ReleaseVolEnv = 38,
    KeynumToVolEnvHold = 39,
    KeynumToVolEnvDecay = 40,
    Instrument = 41,
    // 42 reserved
    KeyRange = 43,
    VelRange = 44,
    StartLoopAddrsCoarseOffset = 45,
    Keynum = 46,
    Velocity = 47,
    InitialAttenuation = 48,
    // 49 reserved
    EndLoopAddrsCoarseOffset = 50,
    CoarseTune = 51,
    FineTune = 52,
    SampleId = 53,
    SampleModes = 54,
    // 55 reserved
    ScaleTuning = 56,
    ExclusiveClass = 57,
    OverridingRootKey = 58,
    // 59 unused
    EndOper = 60
}

/// <summary>
/// SF2 Modulator source controllers.
/// </summary>
public enum ModulatorSource : ushort
{
    NoController = 0,
    NoteOnVelocity = 2,
    NoteOnKeyNum = 3,
    PolyPressure = 10,
    ChannelPressure = 13,
    PitchWheel = 14,
    PitchWheelSensitivity = 16,
    Link = 127
}

/// <summary>
/// Sample link types.
/// </summary>
[Flags]
public enum SampleLink : ushort
{
    MonoSample = 1,
    RightSample = 2,
    LeftSample = 4,
    LinkedSample = 8,
    RomMonoSample = 0x8001,
    RomRightSample = 0x8002,
    RomLeftSample = 0x8004,
    RomLinkedSample = 0x8008
}

/// <summary>
/// Modulator source type (shape of the modulation curve).
/// </summary>
public enum ModulatorSourceType : byte
{
    Linear = 0,
    Concave = 1,
    Convex = 2,
    Switch = 3
}

/// <summary>
/// Modulator source direction.
/// </summary>
public enum ModulatorDirection : byte
{
    Forward = 0,
    Reverse = 1
}

/// <summary>
/// Modulator source polarity.
/// </summary>
public enum ModulatorPolarity : byte
{
    Unipolar = 0,
    Bipolar = 1
}

/// <summary>
/// Modulator source controller type.
/// </summary>
public enum ModulatorControllerType : byte
{
    GeneralController = 0,
    MidiController = 1
}

/// <summary>
/// Modulator transform type.
/// </summary>
public enum ModulatorTransform : ushort
{
    Linear = 0,
    Absolute = 2
}

/// <summary>
/// Sample loop modes.
/// </summary>
public enum SampleLoopMode : ushort
{
    NoLoop = 0,
    LoopContinuously = 1,
    LoopDuringKeyDepression = 3
}

/// <summary>
/// SF2 file parsing error codes.
/// </summary>
public enum Sf2Error
{
    Success,
    PresetIndexNonMonotonic,
    PresetBagCountBad,
    PresetBagGenIndexNonMonotonic,
    PresetBagModIndexNonMonotonic,
    PresetBagGenCountBad,
    PresetBagModCountBad,
    InstrumentIndexNonMonotonic,
    InstrumentBagCountBad,
    InstrumentBagGenIndexNonMonotonic,
    InstrumentBagModIndexNonMonotonic,
    InstrumentBagGenCountBad,
    InstrumentBagModCountBad,
    BadFileName,
    RiffChunkTooLarge,
    RiffChunkTooSmall,
    IfilMissing,
    IsngMissing,
    InamMissing,
    IfilBadLength,
    PhdrChunkBad,
    PbagChunkBad,
    PmodChunkBad,
    PgenChunkBad,
    InstChunkBad,
    IbagChunkBad,
    ImodChunkBad,
    IgenChunkBad,
    ShdrChunkBad,
    FileBroken
}

/// <summary>
/// Range type for key and velocity ranges.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct RangeType
{
    public readonly byte Low;
    public readonly byte High;

    public RangeType(byte low, byte high)
    {
        Low = low;
        High = high;
    }

    public bool Contains(byte value) => value >= Low && value <= High;

    public override string ToString() => $"{Low}-{High}";
}

/// <summary>
/// Generator amount - can be a signed/unsigned value or a range.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct GeneratorAmount
{
    [FieldOffset(0)] public short SignedAmount;
    [FieldOffset(0)] public ushort UnsignedAmount;
    [FieldOffset(0)] public RangeType Range;

    public GeneratorAmount(short value) : this() => SignedAmount = value;
    public GeneratorAmount(ushort value) : this() => UnsignedAmount = value;
    public GeneratorAmount(byte low, byte high) : this() => Range = new RangeType(low, high);
}

/// <summary>
/// SF2 version tag.
/// </summary>
public readonly struct Sf2Version
{
    public readonly ushort Major;
    public readonly ushort Minor;

    public Sf2Version(ushort major, ushort minor)
    {
        Major = major;
        Minor = minor;
    }

    public override string ToString() => $"{Major}.{Minor:D2}";
}

/// <summary>
/// Raw preset header as stored in the SF2 file.
/// </summary>
public struct RawPresetHeader
{
    public byte[] Name; // 20 bytes
    public ushort Preset;
    public ushort Bank;
    public ushort PresetBagIndex;
    public uint Library;
    public uint Genre;
    public uint Morphology;

    public const int Size = 38;

    public string GetName()
    {
        if (Name == null) return string.Empty;
        var len = Array.IndexOf(Name, (byte)0);
        if (len < 0) len = Name.Length;
        return Encoding.ASCII.GetString(Name, 0, len);
    }

    public void SetName(string name)
    {
        Name = new byte[20];
        var bytes = Encoding.ASCII.GetBytes(name ?? string.Empty);
        Array.Copy(bytes, Name, Math.Min(bytes.Length, 20));
    }
}

/// <summary>
/// Raw instrument header as stored in the SF2 file.
/// </summary>
public struct RawInstrumentHeader
{
    public byte[] Name; // 20 bytes
    public ushort InstrumentBagIndex;

    public const int Size = 22;

    public string GetName()
    {
        if (Name == null) return string.Empty;
        var len = Array.IndexOf(Name, (byte)0);
        if (len < 0) len = Name.Length;
        return Encoding.ASCII.GetString(Name, 0, len);
    }

    public void SetName(string name)
    {
        Name = new byte[20];
        var bytes = Encoding.ASCII.GetBytes(name ?? string.Empty);
        Array.Copy(bytes, Name, Math.Min(bytes.Length, 20));
    }
}

/// <summary>
/// Raw sample header as stored in the SF2 file.
/// </summary>
public struct RawSampleHeader
{
    public byte[] Name; // 20 bytes
    public uint Start;
    public uint End;
    public uint StartLoop;
    public uint EndLoop;
    public uint SampleRate;
    public byte OriginalPitch;
    public sbyte PitchCorrection;
    public ushort SampleLink;
    public ushort SampleType;

    public const int Size = 46;

    public string GetName()
    {
        if (Name == null) return string.Empty;
        var len = Array.IndexOf(Name, (byte)0);
        if (len < 0) len = Name.Length;
        return Encoding.ASCII.GetString(Name, 0, len);
    }

    public void SetName(string name)
    {
        Name = new byte[20];
        var bytes = Encoding.ASCII.GetBytes(name ?? string.Empty);
        Array.Copy(bytes, Name, Math.Min(bytes.Length, 20));
    }
}

/// <summary>
/// Raw bag (zone boundary) as stored in the SF2 file.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RawBag
{
    public ushort GenIndex;
    public ushort ModIndex;

    public const int Size = 4;
}

/// <summary>
/// Raw generator as stored in the SF2 file.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RawGenerator
{
    public ushort Operator;
    public GeneratorAmount Amount;

    public const int Size = 4;
}

/// <summary>
/// Raw modulator as stored in the SF2 file.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RawModulator
{
    public ushort SourceOperator;
    public ushort DestOperator;
    public short Amount;
    public ushort AmountSourceOperator;
    public ushort TransformOperator;

    public const int Size = 10;
}
