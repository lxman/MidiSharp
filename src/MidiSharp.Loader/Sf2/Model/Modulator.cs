using MidiSharp.Loader.Sf2.Enums;

namespace MidiSharp.Loader.Sf2.Model;

/// <summary>
/// One modulator entry within a preset or instrument zone.
/// </summary>
public sealed class Modulator
{
    public ushort SourceOperator { get; set; }
    public SFGenerator DestinationOperator { get; set; }
    public short Amount { get; set; }
    public ushort AmountSourceOperator { get; set; }
    public ushort TransformOperator { get; set; }

    public ModulatorSource Source => new(SourceOperator);
    public ModulatorSource AmountSource => new(AmountSourceOperator);
    public SFModSrcTransform Transform => (SFModSrcTransform)TransformOperator;
}

/// <summary>
/// Decoded view of a modulator's 16-bit source operator field (SF2 spec §8.2).
/// </summary>
public readonly struct ModulatorSource(ushort raw)
{
    public ushort Raw => raw;

    /// <summary>Controller index. For General controllers this is an <see cref="SFModSource"/>; for MIDI it's a CC number.</summary>
    public byte Index => (byte)(raw & 0x7F);

    public SFModSrcCC ContinuousController => (SFModSrcCC)((raw >> 7) & 0x1);
    public SFModSrcDirection Direction => (SFModSrcDirection)((raw >> 8) & 0x1);
    public SFModSrcPolarity Polarity => (SFModSrcPolarity)((raw >> 9) & 0x1);
    public SFModSrcType Type => (SFModSrcType)((raw >> 10) & 0x3F);

    /// <summary>Returns the <see cref="SFModSource"/> name when this is a General controller.</summary>
    public SFModSource? AsGeneralController => ContinuousController == SFModSrcCC.General ? (SFModSource)Index : null;
}
