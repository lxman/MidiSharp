namespace Loader.Sf2.Enums;

public enum SFModSource : ushort
{
    NoController = 0,
    NoteOnVelocity = 2,
    NoteOnKeyNum = 3,
    PolyPressure = 10,
    ChannelPressure = 13,
    PitchWheel = 14,
    PitchWheelSensitivity = 16,
    Link = 127,
}

public enum SFModSrcType : byte
{
    Linear = 0,
    Concave = 1,
    Convex = 2,
    Switch = 3,
}

public enum SFModSrcDirection : byte
{
    Forward = 0,
    Reverse = 1,
}

public enum SFModSrcPolarity : byte
{
    Unipolar = 0,
    Bipolar = 1,
}

public enum SFModSrcCC : byte
{
    General = 0,
    Midi = 1,
}

public enum SFModSrcTransform : ushort
{
    Linear = 0,
    Absolute = 2,
}

/// <summary>
/// Indicates which of the two source operators on a modulator a query refers to.
/// </summary>
public enum SFModSrcFrom
{
    ModSource = 0,
    ModAmtSource = 1,
}
