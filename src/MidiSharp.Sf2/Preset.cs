using System.Collections.Generic;

namespace MidiSharp.Sf2;

/// <summary>
/// Represents an SF2 preset - a named sound that can be selected by bank/program.
/// </summary>
public sealed class Preset
{
    private readonly List<Zone> _zones = [];

    /// <summary>
    /// Preset name (up to 20 characters).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// MIDI preset number (0-127).
    /// </summary>
    public ushort PresetNumber { get; set; }

    /// <summary>
    /// MIDI bank number.
    /// </summary>
    public ushort Bank { get; set; }

    /// <summary>
    /// Library tag (reserved).
    /// </summary>
    public uint Library { get; set; }

    /// <summary>
    /// Genre tag (reserved).
    /// </summary>
    public uint Genre { get; set; }

    /// <summary>
    /// Morphology tag (reserved).
    /// </summary>
    public uint Morphology { get; set; }

    /// <summary>
    /// The zones that define this preset.
    /// </summary>
    public IReadOnlyList<Zone> Zones => _zones;

    /// <summary>
    /// Gets the global zone (if present) which contains default generators.
    /// </summary>
    public Zone? GlobalZone => _zones.Count > 0 && _zones[0].IsGlobal ? _zones[0] : null;

    public void AddZone(Zone zone)
    {
        zone.Index = _zones.Count;
        _zones.Add(zone);
    }

    public Zone GetZone(int index) => _zones[index];

    /// <summary>
    /// Creates a Preset from raw header data.
    /// </summary>
    public static Preset FromRawHeader(RawPresetHeader raw)
    {
        return new Preset
        {
            Name = raw.GetName(),
            PresetNumber = raw.Preset,
            Bank = raw.Bank,
            Library = raw.Library,
            Genre = raw.Genre,
            Morphology = raw.Morphology
        };
    }

    /// <summary>
    /// Converts to raw format for writing.
    /// </summary>
    public RawPresetHeader ToRawHeader(ushort bagIndex)
    {
        var raw = new RawPresetHeader
        {
            Preset = PresetNumber,
            Bank = Bank,
            PresetBagIndex = bagIndex,
            Library = Library,
            Genre = Genre,
            Morphology = Morphology
        };
        raw.SetName(Name);
        return raw;
    }

    public override string ToString()
    {
        return $"{Bank:D3}:{PresetNumber:D3} {Name}";
    }
}
