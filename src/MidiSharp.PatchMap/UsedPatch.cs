using System.Collections.Generic;

namespace MidiSharp.PatchMap;

/// <summary>
/// One patch a song actually calls for: the resolved (bank, program) that is active on a
/// channel when a note sounds, the channels it sounds on, and — when a base bank is supplied
/// to the analyzer — the instrument name that would normally play there.
/// </summary>
public sealed class UsedPatch
{
    /// <summary>Resolved MIDI bank (128 = percussion).</summary>
    public int Bank { get; init; }

    /// <summary>MIDI program (0..127).</summary>
    public int Program { get; init; }

    /// <summary>MIDI channels (0..15) on which this patch sounds at least one note, ascending.</summary>
    public IReadOnlyList<int> Channels { get; init; } = [];

    /// <summary>True when this is the percussion bank (a drum kit, swapped whole).</summary>
    public bool IsDrum { get; init; }

    /// <summary>
    /// The base font's name for what would play here (after the same bank-0 fallback the synth
    /// applies), or null if the analyzer had no base bank or nothing matched.
    /// </summary>
    public string? BaseName { get; init; }
}
