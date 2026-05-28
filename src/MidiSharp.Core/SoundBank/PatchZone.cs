using System;
using System.Collections.Generic;

namespace MidiSharp.SoundBank;

/// <summary>
/// One playable region. A NoteOn matches a zone iff every activation condition
/// passes; each matching zone allocates one voice. Pre-flattened from any
/// source-format hierarchy by the loader — no inheritance to resolve here.
/// </summary>
/// <remarks>
/// Optional features are nullable (filter, mod envelope, LFOs, keyswitch,
/// round-robin); null means "this feature doesn't apply to this zone." The
/// synth checks once per NoteOn — zero overhead when absent.
/// </remarks>
public sealed class PatchZone
{
    // ─── Activation conditions ──────────────────────────────────────

    public KeyRange Keys { get; init; } = new(0, 127);

    public VelocityRange Velocities { get; init; } = new(0, 127);

    /// <summary>SFZ locc/hicc gates (AND semantics). Empty for SF2/SF3/DLS.</summary>
    public IReadOnlyList<CCGate> CCGates { get; init; } = Array.Empty<CCGate>();

    /// <summary>SFZ sw_* keyswitch. Null elsewhere.</summary>
    public KeySwitch? KeySwitch { get; init; }

    /// <summary>SFZ seq_* round-robin. Null elsewhere.</summary>
    public RoundRobin? RoundRobin { get; init; }

    /// <summary>SF2 ExclusiveClass / SFZ group= + off_by=. Null = no grouping.</summary>
    public int? ExclusiveGroup { get; init; }

    // ─── Sample reference ───────────────────────────────────────────

    public SampleRef Sample { get; init; } = new();

    // ─── Static playback parameters ─────────────────────────────────

    public PitchSettings Pitch { get; init; } = new();

    public LevelSettings Level { get; init; } = new();

    // ─── Time-varying modulators ────────────────────────────────────

    /// <summary>Always present; the volume envelope is non-optional.</summary>
    public EnvelopeSettings VolumeEnvelope { get; init; } = new() { SustainLevel = 1.0 };

    public EnvelopeSettings? ModulationEnvelope { get; init; }

    public LFOSettings? VibratoLFO { get; init; }

    public LFOSettings? ModulationLFO { get; init; }

    public FilterSettings? Filter { get; init; }

    // ─── Sends (0..1) ───────────────────────────────────────────────

    public double ReverbSend { get; init; }

    public double ChorusSend { get; init; }

    // ─── Routing matrix ─────────────────────────────────────────────

    public IReadOnlyList<ModulationRoute> Routes { get; init; } = Array.Empty<ModulationRoute>();
}
