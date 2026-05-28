using System.Collections.Generic;
using System.Linq;

namespace MidiSharp.Sf2;

/// <summary>
/// Represents an SF2 zone - a region defined by generators and modulators
/// that maps to an instrument (preset zone) or sample (instrument zone).
/// </summary>
public sealed class Zone
{
    private readonly List<Generator> _generators = [];
    private readonly List<Modulator> _modulators = [];

    /// <summary>
    /// The zone index within its parent preset or instrument.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// The generators that define this zone's parameters.
    /// </summary>
    public IReadOnlyList<Generator> Generators => _generators;

    /// <summary>
    /// The modulators that define dynamic parameter changes.
    /// </summary>
    public IReadOnlyList<Modulator> Modulators => _modulators;

    /// <summary>
    /// For preset zones: the instrument index this zone references.
    /// -1 if this is a global zone or doesn't reference an instrument.
    /// </summary>
    public int InstrumentIndex { get; set; } = -1;

    /// <summary>
    /// For instrument zones: the sample this zone references.
    /// </summary>
    public SampleHeader? Sample { get; set; }

    /// <summary>
    /// For instrument zones: the extracted sample data.
    /// </summary>
    public Sample? ExcisedSample { get; set; }

    /// <summary>
    /// Whether this is a global zone (no instrument/sample reference).
    /// </summary>
    public bool IsGlobal => InstrumentIndex < 0 && Sample == null;

    /// <summary>
    /// Gets the key range for this zone, or null if not specified.
    /// </summary>
    public RangeType? KeyRange => GetGenerator(GeneratorType.KeyRange)?.Range;

    /// <summary>
    /// Gets the velocity range for this zone, or null if not specified.
    /// </summary>
    public RangeType? VelocityRange => GetGenerator(GeneratorType.VelRange)?.Range;

    public void AddGenerator(Generator generator)
    {
        _generators.Add(generator);
    }

    public void AddModulator(Modulator modulator)
    {
        _modulators.Add(modulator);
    }

    public void RemoveLastGenerator()
    {
        if (_generators.Count > 0)
            _generators.RemoveAt(_generators.Count - 1);
    }

    /// <summary>
    /// Gets a generator by type, or null if not present.
    /// </summary>
    public Generator? GetGenerator(GeneratorType type)
    {
        return _generators.FirstOrDefault(g => g.Type == type);
    }

    /// <summary>
    /// Gets the value of a generator, or a default value if not present.
    /// </summary>
    public short GetGeneratorValue(GeneratorType type, short defaultValue = 0)
    {
        var gen = GetGenerator(type);
        return gen?.SignedValue ?? defaultValue;
    }

    /// <summary>
    /// Checks if a MIDI note and velocity fall within this zone's ranges.
    /// </summary>
    public bool MatchesNoteAndVelocity(byte note, byte velocity)
    {
        var keyRange = KeyRange;
        if (keyRange.HasValue && !keyRange.Value.Contains(note))
            return false;

        var velRange = VelocityRange;
        if (velRange.HasValue && !velRange.Value.Contains(velocity))
            return false;

        return true;
    }

    public override string ToString()
    {
        if (InstrumentIndex >= 0)
            return $"Zone {Index} -> Instrument {InstrumentIndex}";
        if (Sample != null)
            return $"Zone {Index} -> Sample '{Sample.Name}'";
        return $"Zone {Index} (Global)";
    }
}
