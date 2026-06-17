namespace MidiSharp.SoundBank;

/// <summary>
/// DAHDSR envelope parameters in domain-natural units (seconds, linear amplitude).
/// SF2 timecent and centibel encodings are converted at load time.
/// </summary>
/// <remarks>
/// A stage time of 0 means "instantaneous" — the synth skips that phase.
/// <see cref="SustainLevel"/> is 0..1 linear amplitude for the volume envelope
/// and 0..1 linear amount for the modulation envelope (whose destination is
/// determined by the zone's routes).
/// </remarks>
public sealed class EnvelopeSettings
{
    /// <summary>Delay before attack starts, in seconds.</summary>
    public double DelaySeconds { get; init; }

    /// <summary>Attack time (0 → peak), in seconds. Linear in dB.</summary>
    public double AttackSeconds { get; init; }

    /// <summary>Hold time at peak, in seconds.</summary>
    public double HoldSeconds { get; init; }

    /// <summary>Decay time (peak → sustain), in seconds. Linear in dB.</summary>
    public double DecaySeconds { get; init; }

    /// <summary>Sustain level, 0..1 normalized.</summary>
    public double SustainLevel { get; init; } = 1.0;

    /// <summary>Release time (sustain → 0), in seconds. Linear in dB.</summary>
    public double ReleaseSeconds { get; init; }

    /// <summary>
    /// Per-key scaling of hold time. Hold is offset by N cents (1200 = 2× time)
    /// per key away from middle C (key 60). 0 = no scaling.
    /// </summary>
    public double KeynumToHoldCentsPerKey { get; init; }

    /// <summary>
    /// Per-key scaling of decay time. Decay is offset by N cents (1200 = 2× time)
    /// per key away from middle C (key 60). 0 = no scaling.
    /// </summary>
    public double KeynumToDecayCentsPerKey { get; init; }

    // ── Velocity modulation (SFZ ampeg_vel2*; 0 = none) ──────────────
    // Each adds (velocity/127) × this to the corresponding stage at NoteOn: times in seconds,
    // sustain as a 0..1 level offset. Matches sfizz (stage = base + velocityNorm × vel2stage).

    /// <summary>Velocity → delay time, seconds.</summary>
    public double VelToDelaySeconds { get; init; }

    /// <summary>Velocity → attack time, seconds (typically negative: harder = snappier).</summary>
    public double VelToAttackSeconds { get; init; }

    /// <summary>Velocity → hold time, seconds.</summary>
    public double VelToHoldSeconds { get; init; }

    /// <summary>Velocity → decay time, seconds.</summary>
    public double VelToDecaySeconds { get; init; }

    /// <summary>Velocity → release time, seconds.</summary>
    public double VelToReleaseSeconds { get; init; }

    /// <summary>Velocity → sustain level, as a 0..1 offset.</summary>
    public double VelToSustainLevel { get; init; }
}
