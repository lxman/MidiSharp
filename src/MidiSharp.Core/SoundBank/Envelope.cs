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
    /// ARIA <c>ampeg_release_shape</c>: curvature of the release segment. 0 (the default for every
    /// format) keeps the dB-linear exponential release; negative values make it more convex (drops
    /// faster at first then tails), positive more concave. Only the release segment is shaped.
    /// </summary>
    public double ReleaseShape { get; init; }

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

    /// <summary>
    /// SFZ CC modulations of the envelope stages (ampeg_{stage}_oncc / the bare-cc alias), evaluated
    /// from the LIVE controller at note-on rather than baked at a static seed — this is the
    /// segment-start evaluation SFZ specifies (and what ampeg_dynamic builds on). Null for SF2/DLS and
    /// SFZ zones without envelope CC modulation, which keeps those byte-identical.
    /// </summary>
    public EnvCcMod[]? CcMods { get; init; }

    /// <summary>
    /// SFZ <c>ampeg_dynamic=1</c>: the envelope re-reads its CC-modulated durations/sustain while a
    /// modulating CC moves. We evaluate at note-on (covers the common mod-wheel-set-before-the-note
    /// case); true mid-note recalculation is not yet done. Default (0) evaluates once at the start.
    /// </summary>
    public bool Dynamic { get; init; }
}

/// <summary>A DAHDSR envelope stage that a CC can modulate (SFZ ampeg_{stage}).</summary>
public enum EnvStage { Delay, Attack, Hold, Decay, Sustain, Release }

/// <summary>
/// One CC modulation of an envelope stage: at the live controller value, adds <see cref="Amount"/> ×
/// curve(cc/127) to the stage — seconds for the time stages, percent for <see cref="EnvStage.Sustain"/>.
/// </summary>
public readonly struct EnvCcMod(EnvStage stage, int cc, double amount, int curve)
{
    public EnvStage Stage { get; } = stage;
    public int Cc { get; } = cc;
    public double Amount { get; } = amount;
    public int Curve { get; } = curve;
}
