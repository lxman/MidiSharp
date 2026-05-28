namespace MidiSharp.Sf2;

/// <summary>
/// Represents an SF2 generator item that controls sound parameters.
/// </summary>
public sealed class Generator
{
    /// <summary>
    /// The generator type/operator.
    /// </summary>
    public GeneratorType Type { get; set; }

    /// <summary>
    /// The generator amount value.
    /// </summary>
    public GeneratorAmount Amount { get; set; }

    public Generator()
    {
    }

    public Generator(GeneratorType type, GeneratorAmount amount)
    {
        Type = type;
        Amount = amount;
    }

    public Generator(GeneratorType type, short value)
    {
        Type = type;
        Amount = new GeneratorAmount(value);
    }

    public Generator(GeneratorType type, ushort value)
    {
        Type = type;
        Amount = new GeneratorAmount(value);
    }

    public Generator(GeneratorType type, byte low, byte high)
    {
        Type = type;
        Amount = new GeneratorAmount(low, high);
    }

    /// <summary>
    /// Gets the signed amount value.
    /// </summary>
    public short SignedValue => Amount.SignedAmount;

    /// <summary>
    /// Gets the unsigned amount value.
    /// </summary>
    public ushort UnsignedValue => Amount.UnsignedAmount;

    /// <summary>
    /// Gets the range value.
    /// </summary>
    public RangeType Range => Amount.Range;

    /// <summary>
    /// Converts to raw format for writing.
    /// </summary>
    public RawGenerator ToRaw() => new RawGenerator
    {
        Operator = (ushort)Type,
        Amount = Amount
    };

    /// <summary>
    /// Creates a Generator from raw data.
    /// </summary>
    public static Generator FromRaw(RawGenerator raw) => new Generator
    {
        Type = (GeneratorType)raw.Operator,
        Amount = raw.Amount
    };

    public override string ToString()
    {
        return Type switch
        {
            GeneratorType.KeyRange or GeneratorType.VelRange => $"{Type}: {Range}",
            GeneratorType.Instrument or GeneratorType.SampleId => $"{Type}: {UnsignedValue}",
            _ => $"{Type}: {SignedValue}"
        };
    }
}
