namespace MidiSharp.Sf2;

/// <summary>
/// Decoded modulator source information.
/// </summary>
public sealed class ModulatorSourceInfo
{
    /// <summary>
    /// Controller index (0-127 for general, 0-127 for MIDI CC).
    /// </summary>
    public byte ControllerIndex { get; set; }

    /// <summary>
    /// Controller type - general or MIDI CC.
    /// </summary>
    public ModulatorControllerType ControllerType { get; set; }

    /// <summary>
    /// Source direction - forward or reverse.
    /// </summary>
    public ModulatorDirection Direction { get; set; }

    /// <summary>
    /// Source polarity - unipolar or bipolar.
    /// </summary>
    public ModulatorPolarity Polarity { get; set; }

    /// <summary>
    /// Source curve type - linear, concave, convex, or switch.
    /// </summary>
    public ModulatorSourceType SourceType { get; set; }

    /// <summary>
    /// Decodes a 16-bit source operator value.
    /// </summary>
    public static ModulatorSourceInfo Decode(ushort sourceOperator)
    {
        return new ModulatorSourceInfo
        {
            ControllerIndex = (byte)(sourceOperator & 0x7F),
            ControllerType = (ModulatorControllerType)((sourceOperator >> 7) & 0x01),
            Direction = (ModulatorDirection)((sourceOperator >> 8) & 0x01),
            Polarity = (ModulatorPolarity)((sourceOperator >> 9) & 0x01),
            SourceType = (ModulatorSourceType)((sourceOperator >> 10) & 0x3F)
        };
    }

    /// <summary>
    /// Encodes back to a 16-bit source operator value.
    /// </summary>
    public ushort Encode()
    {
        return (ushort)(
            (ControllerIndex & 0x7F) |
            ((int)ControllerType << 7) |
            ((int)Direction << 8) |
            ((int)Polarity << 9) |
            ((int)SourceType << 10)
        );
    }

    /// <summary>
    /// Gets a human-readable description of the controller.
    /// </summary>
    public string GetControllerName()
    {
        if (ControllerType == ModulatorControllerType.MidiController)
        {
            return $"MIDI CC {ControllerIndex}";
        }

        return (ModulatorSource)ControllerIndex switch
        {
            ModulatorSource.NoController => "None",
            ModulatorSource.NoteOnVelocity => "Note-On Velocity",
            ModulatorSource.NoteOnKeyNum => "Note-On Key Number",
            ModulatorSource.PolyPressure => "Poly Pressure",
            ModulatorSource.ChannelPressure => "Channel Pressure",
            ModulatorSource.PitchWheel => "Pitch Wheel",
            ModulatorSource.PitchWheelSensitivity => "Pitch Wheel Sensitivity",
            ModulatorSource.Link => "Link",
            _ => $"General Controller {ControllerIndex}"
        };
    }

    public override string ToString()
    {
        return $"{GetControllerName()} ({Direction}, {Polarity}, {SourceType})";
    }
}

/// <summary>
/// Represents an SF2 modulator that routes a source controller to a destination generator.
/// </summary>
public sealed class Modulator
{
    /// <summary>
    /// The raw source operator value.
    /// </summary>
    public ushort SourceOperator { get; set; }

    /// <summary>
    /// The destination generator.
    /// </summary>
    public GeneratorType Destination { get; set; }

    /// <summary>
    /// The modulation amount.
    /// </summary>
    public short Amount { get; set; }

    /// <summary>
    /// The raw amount source operator value.
    /// </summary>
    public ushort AmountSourceOperator { get; set; }

    /// <summary>
    /// The transform type.
    /// </summary>
    public ModulatorTransform Transform { get; set; }

    /// <summary>
    /// Gets the decoded source information.
    /// </summary>
    public ModulatorSourceInfo Source => ModulatorSourceInfo.Decode(SourceOperator);

    /// <summary>
    /// Gets the decoded amount source information.
    /// </summary>
    public ModulatorSourceInfo AmountSource => ModulatorSourceInfo.Decode(AmountSourceOperator);

    public Modulator()
    {
    }

    public Modulator(RawModulator raw)
    {
        SourceOperator = raw.SourceOperator;
        Destination = (GeneratorType)raw.DestOperator;
        Amount = raw.Amount;
        AmountSourceOperator = raw.AmountSourceOperator;
        Transform = (ModulatorTransform)raw.TransformOperator;
    }

    /// <summary>
    /// Converts to raw format for writing.
    /// </summary>
    public RawModulator ToRaw() => new RawModulator
    {
        SourceOperator = SourceOperator,
        DestOperator = (ushort)Destination,
        Amount = Amount,
        AmountSourceOperator = AmountSourceOperator,
        TransformOperator = (ushort)Transform
    };

    /// <summary>
    /// Creates a Modulator from raw data.
    /// </summary>
    public static Modulator FromRaw(RawModulator raw) => new Modulator(raw);

    public override string ToString()
    {
        return $"{Source} -> {Destination} (Amount: {Amount}, Transform: {Transform})";
    }
}
