using System.Collections.Generic;

namespace MidiSharp.PatchMap;

/// <summary>
/// What a single MIDI track sounds like as the song plays — the unit the user thinks in
/// ("Violoncello"), independent of which channels/programs its notes happen to wander onto.
/// A per-track override (see <see cref="PatchMapSession.SetTrackOverride"/>) is keyed on
/// <see cref="TrackIndex"/>, with <see cref="Name"/> shown as its label.
/// </summary>
public sealed class TrackUsage
{
    /// <summary>The 0-based index of the track in the file — the stable override key.</summary>
    public int TrackIndex { get; init; }

    /// <summary>The track name (from its TrackName meta event), or null if unnamed.</summary>
    public string? Name { get; init; }

    /// <summary>MIDI channels (0..15) this track sounds at least one note on, ascending.</summary>
    public IReadOnlyList<int> Channels { get; init; } = [];

    /// <summary>Distinct resolved programs (0..127) this track's notes play, ascending.</summary>
    public IReadOnlyList<int> Programs { get; init; } = [];

    /// <summary>
    /// The base font's name for what this track plays first (after the synth's bank-0 fallback),
    /// or null if the analyzer had no base bank, nothing matched, or the track sounds no notes.
    /// </summary>
    public string? BaseName { get; init; }

    /// <summary>True when the track sounds at least one note.</summary>
    public bool HasNotes => Channels.Count > 0;
}
