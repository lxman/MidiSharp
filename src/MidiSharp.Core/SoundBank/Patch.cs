using System;
using System.Collections.Generic;

namespace MidiSharp.SoundBank;

/// <summary>
/// What a Bank Select + Program Change resolves to. Owns a flat list of zones;
/// each matching zone produces one voice on NoteOn.
/// </summary>
public sealed class Patch
{
    /// <summary>MIDI bank (0..127 typical; 128 = GM drum bank).</summary>
    public int Bank { get; init; }

    /// <summary>MIDI program (0..127).</summary>
    public int Program { get; init; }

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<PatchZone> Zones { get; init; } = Array.Empty<PatchZone>();
}
