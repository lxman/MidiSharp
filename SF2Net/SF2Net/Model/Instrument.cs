namespace SF2Net;

/// <summary>
/// A SoundFont instrument: a named collection of zones that map MIDI key/velocity space to samples.
/// </summary>
public sealed class Instrument
{
    public string Name { get; set; } = string.Empty;
    public int Index { get; set; }
    public List<Zone> Zones { get; } = [];

    /// <summary>
    /// The instrument's global zone, if any. Per SF2 spec §7.7, a global zone is the first zone
    /// of an instrument whose generator list does not end with a <c>SampleID</c> generator — its
    /// generators and modulators act as defaults inherited by every other zone in the instrument.
    /// Returns <c>null</c> when no global zone is present.
    /// </summary>
    public Zone? GlobalZone =>
        Zones.Count > 0 && Zones[0].Sample is null ? Zones[0] : null;

    public override string ToString() => $"#{Index}: {Name}";
}
