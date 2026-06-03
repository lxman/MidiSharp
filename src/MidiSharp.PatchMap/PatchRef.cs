using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap;

/// <summary>
/// An override target: the patch at (<see cref="Bank"/>, <see cref="Program"/>) inside source
/// font <see cref="Source"/> that should answer in place of the base font's patch at some
/// logical address.
/// </summary>
public readonly struct PatchRef
{
    public PatchRef(IRBank source, int bank, int program)
    {
        Source = source;
        Bank = bank;
        Program = program;
    }

    /// <summary>The preloaded source font this instrument comes from.</summary>
    public IRBank Source { get; }

    /// <summary>The source patch's bank within <see cref="Source"/>.</summary>
    public int Bank { get; }

    /// <summary>The source patch's program within <see cref="Source"/>.</summary>
    public int Program { get; }
}
