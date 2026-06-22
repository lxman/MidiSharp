using System.Collections.Generic;
using MidiSharp.Loader.Sf2.Enums;

namespace MidiSharp.Loader.Sf2.Model;

/// <summary>
/// A zone — a container of generators and modulators inside a preset or instrument.
/// </summary>
public sealed class Zone
{
    public int Index { get; set; }

    public List<Generator> Generators { get; } = [];
    public List<Modulator> Modulators { get; } = [];

    /// <summary>For preset zones: the instrument index this zone references, or <c>-1</c> if none.</summary>
    public int InstrumentIndex { get; set; } = -1;

    /// <summary>For instrument zones: the sample header this zone references, or <c>null</c>.</summary>
    public SampleHeader? Sample { get; set; }

    /// <summary>Used during preset extraction.</summary>
    internal ExcisedSample? ExcisedSample { get; set; }

    internal void RemoveLastGenerator() => Generators.RemoveAt(Generators.Count - 1);

    /// <summary>
    /// Returns true if this zone's <c>KeyRange</c> and <c>VelRange</c> generators (if present)
    /// admit the given MIDI key (0-127) and velocity (0-127). Per SF2 spec §8.1.2, a missing
    /// range generator is equivalent to the full 0-127 range, so a zone with neither generator
    /// always matches.
    /// </summary>
    public bool MatchesKeyVelocity(int key, int velocity)
    {
        foreach (Generator? gen in Generators)
        {
            switch (gen.Operator)
            {
                case SFGenerator.KeyRange:
                {
                    ByteRange r = gen.Amount.Range;
                    if (key < r.Low || key > r.High) return false;
                    break;
                }
                case SFGenerator.VelRange:
                {
                    ByteRange r = gen.Amount.Range;
                    if (velocity < r.Low || velocity > r.High) return false;
                    break;
                }
            }
        }
        return true;
    }
}

internal sealed class ExcisedSample
{
    public byte[] Data { get; set; } = [];
    public SampleHeader Header { get; set; } = new();
}
