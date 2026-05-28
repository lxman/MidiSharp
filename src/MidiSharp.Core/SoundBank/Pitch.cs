namespace MidiSharp.SoundBank;

/// <summary>
/// Zone-wide tuning offsets. Combines with <see cref="SampleRef.FineTuneCents"/> /
/// <see cref="SampleRef.CoarseTuneSemitones"/> at voice setup; both fields are
/// kept so loaders can preserve the source format's distinction between
/// zone-level and per-sample tuning.
/// </summary>
public sealed class PitchSettings
{
    /// <summary>Fine tune in cents (-100..+100).</summary>
    public double FineTuneCents { get; init; }

    /// <summary>Coarse tune in semitones (-120..+120).</summary>
    public double CoarseTuneSemitones { get; init; }

    /// <summary>
    /// Modulation-envelope contribution to pitch, in cents at envelope peak
    /// (SF2 <c>ModEnvToPitch</c> generator). Multiplied by the envelope's
    /// current value (0..1) at runtime. 0 = no contribution.
    /// </summary>
    public double ModulationEnvelopeDepthCents { get; init; }
}

/// <summary>
/// Per-zone level and pan. Pan is signed -1..+1 (full left to full right).
/// Attenuation is non-negative by convention — synthesis only attenuates,
/// never boosts (boosting clips).
/// </summary>
public sealed class LevelSettings
{
    /// <summary>Attenuation in dB. 0 = unity gain; positive = quieter.</summary>
    public double AttenuationDb { get; init; }

    /// <summary>Pan: -1.0 = full left, 0 = center, +1.0 = full right.</summary>
    public double Pan { get; init; }
}
