namespace MidiSharp.SoundBank;

/// <summary>
/// One segment of an SFZ v2 flex envelope (egN_timeX / egN_levelX): ramp to <see cref="Level"/> over
/// <see cref="TimeSeconds"/> from the previous segment's level.
/// </summary>
public readonly struct EgStage
{
    public EgStage(double timeSeconds, double level)
    {
        TimeSeconds = timeSeconds;
        Level = level;
    }

    public double TimeSeconds { get; }
    public double Level { get; }
}

/// <summary>A destination an SFZ flex EG drives (egN_pitch / egN_cutoff / egN_volume), with a depth.</summary>
public sealed class EgTarget
{
    public LfoDestination Destination { get; init; }

    /// <summary>Modulation depth in the destination's units (cents, dB).</summary>
    public double Depth { get; init; }

    /// <summary>CCs that scale <see cref="Depth"/> (egN_{target}_onccX). Null = none.</summary>
    public LfoCcDepth[]? DepthCc { get; init; }
}

/// <summary>
/// An SFZ v2 generic flex envelope (<c>egN</c>): a sequence of timed level segments with a sustain
/// point, routed to one or more <see cref="EgTarget"/>s. Runs per-sample in the voice alongside the
/// DAHDSR amp/mod envelopes; present only on SFZ zones that declare <c>egN_*</c> opcodes.
/// </summary>
public sealed class GenericEg
{
    /// <summary>Segments in order (index = SFZ point number). Always at least one.</summary>
    public EgStage[] Stages { get; init; } = [];

    /// <summary>Sustain point index (egN_sustain): the EG holds at this segment's level. -1 = run to end.</summary>
    public int SustainStage { get; init; } = -1;

    public EgTarget[] Targets { get; init; } = [];
}
