using System.Collections.Generic;

namespace MidiSharp.Sf2;

/// <summary>
/// Represents an SF2 instrument - a collection of zones that map to samples.
/// </summary>
public sealed class Instrument
{
    private readonly List<Zone> _zones = [];

    /// <summary>
    /// Instrument name (up to 20 characters).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Index of this instrument in the instrument list.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// The zones that define this instrument.
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
    /// Creates an Instrument from raw header data.
    /// </summary>
    public static Instrument FromRawHeader(RawInstrumentHeader raw, int index)
    {
        return new Instrument
        {
            Index = index,
            Name = raw.GetName()
        };
    }

    /// <summary>
    /// Converts to raw format for writing.
    /// </summary>
    public RawInstrumentHeader ToRawHeader(ushort bagIndex)
    {
        var raw = new RawInstrumentHeader
        {
            InstrumentBagIndex = bagIndex
        };
        raw.SetName(Name);
        return raw;
    }

    /// <summary>
    /// Finds zones that match the given note and velocity.
    /// </summary>
    public IEnumerable<Zone> FindMatchingZones(byte note, byte velocity)
    {
        foreach (var zone in _zones)
        {
            if (!zone.IsGlobal && zone.MatchesNoteAndVelocity(note, velocity))
                yield return zone;
        }
    }

    public override string ToString()
    {
        return $"{Name} ({_zones.Count} zones)";
    }
}
