namespace MidiSharp.SoundBank;

/// <summary>
/// LFO parameters in domain-natural units. Frequency is Hz (not absolute cents);
/// depths are signed (positive = peaks raise pitch/volume/cutoff; negative = lower).
/// </summary>
/// <remarks>
/// Two LFO slots are independently optional on a zone: <c>VibratoLFO</c>
/// (traditionally pitch-only) and <c>ModulationLFO</c> (traditionally volume +
/// filter). The names reflect SF2 convention but the IR doesn't enforce it —
/// a VibratoLFO with non-zero <see cref="VolumeDepthDb"/> is well-defined.
/// </remarks>
public sealed class LFOSettings
{
    /// <summary>Delay before the LFO starts oscillating, in seconds.</summary>
    public double DelaySeconds { get; init; }

    /// <summary>Oscillation frequency in Hz (typically 0.1-20).</summary>
    public double FrequencyHz { get; init; }

    /// <summary>Pitch modulation depth in cents (signed). 0 = no pitch mod.</summary>
    public double PitchDepthCents { get; init; }

    /// <summary>Volume modulation depth in dB (signed; tremolo). 0 = no volume mod.</summary>
    public double VolumeDepthDb { get; init; }

    /// <summary>Filter cutoff modulation depth in cents (signed; sweep). 0 = no filter mod.</summary>
    public double FilterDepthCents { get; init; }
}
