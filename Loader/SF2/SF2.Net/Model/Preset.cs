namespace SF2.Net;

/// <summary>
/// A SoundFont preset. Each preset belongs to a (Bank, Number) pair and contains one or more zones.
/// </summary>
public sealed class Preset
{
    public string Name { get; set; } = string.Empty;
    public ushort Number { get; set; }
    public ushort Bank { get; set; }
    public uint Library { get; set; }
    public uint Genre { get; set; }
    public uint Morphology { get; set; }

    public List<Zone> Zones { get; } = [];

    /// <summary>
    /// The preset's global zone, if any. Per SF2 spec §7.3, a global zone is the first zone
    /// of a preset whose generator list does not end with an <c>Instrument</c> generator — its
    /// generators and modulators act as defaults inherited by every other zone in the preset.
    /// Returns <c>null</c> when no global zone is present.
    /// </summary>
    public Zone? GlobalZone =>
        Zones.Count > 0 && Zones[0].InstrumentIndex < 0 ? Zones[0] : null;

    public override string ToString() => $"Bank {Bank} Preset {Number}: {Name}";
}
