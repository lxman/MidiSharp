namespace MidiSharp.SoundBank;

/// <summary>
/// Inclusive MIDI key range. Default (0, 127) means "all keys."
/// </summary>
public readonly record struct KeyRange(byte Low, byte High)
{
    public bool Contains(int key) => key >= Low && key <= High;
}

/// <summary>
/// Inclusive MIDI velocity range. Default (0, 127) means "all velocities."
/// </summary>
public readonly record struct VelocityRange(byte Low, byte High)
{
    public bool Contains(int velocity) => velocity >= Low && velocity <= High;
}

/// <summary>
/// SFZ-style continuous-controller gate: a zone is active iff the named CC's
/// current value is within [Low, High] (inclusive). A zone with multiple gates
/// requires ALL of them to be satisfied (AND semantics).
/// </summary>
public readonly record struct CCGate(byte Controller, byte Low, byte High)
{
    public bool Contains(int value) => value >= Low && value <= High;
}

/// <summary>
/// SFZ-style keyswitching. Keys in [Low, High] are switch keys — pressing one
/// selects an articulation instead of sounding a note. A zone carrying this is
/// active only when the channel's currently-selected switch key equals
/// <see cref="SelectingKey"/> (SFZ <c>sw_last</c>). <see cref="Default"/> is the
/// switch key treated as selected before any has been pressed (<c>sw_default</c>).
/// </summary>
/// <remarks>
/// Per-channel selection is runtime state held by the synth's channel, not here —
/// this record is immutable IR shared across channels and voices.
/// </remarks>
public readonly record struct KeySwitch(byte Low, byte High, byte SelectingKey, byte Default);

/// <summary>
/// SFZ-style round-robin. This zone plays on the <see cref="Position"/>-th of
/// every <see cref="Length"/> consecutive matching NoteOns (0-indexed).
/// </summary>
public readonly record struct RoundRobin(int Position, int Length);
