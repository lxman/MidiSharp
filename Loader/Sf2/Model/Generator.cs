using MidiSharp.Loader.Sf2.Enums;

namespace MidiSharp.Loader.Sf2.Model;

/// <summary>
/// One generator entry within a preset or instrument zone.
/// </summary>
public sealed class Generator
{
    public SFGenerator Operator { get; set; }
    public GeneratorAmount Amount { get; set; }

    public Generator() { }

    public Generator(SFGenerator op, GeneratorAmount amount)
    {
        Operator = op;
        Amount = amount;
    }

    public override string ToString() => $"{Operator}={Amount}";
}
