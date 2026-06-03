namespace MidiSharp.SoundBank;

/// <summary>
/// Resolves the effective MIDI bank number from raw Bank Select state, matching the
/// synth's NoteOn lookup. Pure and shared so that offline analysis (e.g. listing the
/// patches a song uses) resolves banks identically to live playback, with no drift.
/// </summary>
public static class BankResolution
{
    /// <summary>GM percussion bank (MIDI channel 10 routes here by default).</summary>
    public const int GmDrumBank = 128;

    /// <summary>
    /// The effective bank from Bank Select MSB/LSB and drum state. A drum part forces its
    /// drum bank regardless of CC 0/32; otherwise a non-zero LSB (GS variation number) wins
    /// over the MSB (XG), matching the synth. The NoteOn path still falls back to bank 0 when
    /// an exact (bank, program) match misses, so an ambiguous choice here is recoverable.
    /// </summary>
    /// <param name="bankMsb">Raw CC 0 value (0..127).</param>
    /// <param name="bankLsb">Raw CC 32 value (0..127).</param>
    /// <param name="isDrumPart">Whether the channel is a percussion part.</param>
    /// <param name="drumBank">The drum bank to use when <paramref name="isDrumPart"/> (128 = MAP1, 127 = MAP2).</param>
    public static int Resolve(int bankMsb, int bankLsb, bool isDrumPart, int drumBank = GmDrumBank)
    {
        if (isDrumPart) return drumBank;
        return bankLsb != 0 ? bankLsb : bankMsb;
    }
}
