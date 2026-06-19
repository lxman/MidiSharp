namespace MidiSharp.Synth;

/// <summary>
/// Identity of a mixer "instrument": the (bank, program) the note's patch resolved to. This is the
/// default, stable selector from the mixer plan — the bass keeps program 32, the oboe keeps program
/// 68 — so a fader bound to it never leaks onto a different part when a channel/track slot is reused
/// later in the piece. Per-track overrides resolve to the synthetic
/// (<c>SoundBankComposer.TrackOverrideBank</c>, trackIndex) address, which is just another stable id.
/// </summary>
public readonly record struct InstrumentId(int Bank, int Program);

/// <summary>
/// Per-instrument mixer state (Tier 1): a <b>trim</b> that rides on top of the file's own CC7/CC10
/// automation — the song's dynamics survive. All defaults are true no-ops, so an instrument whose
/// mix is never touched renders bit-identically to the pre-mixer engine (the mixer is dormant until a
/// control moves). Mutable on purpose: a fader/UI edit mutates the live instance in place, so notes
/// already sounding pick the change up on their next block.
/// </summary>
public sealed class InstrumentMix
{
    /// <summary>Gain trim in dB (positive = louder). Folded into the voice's attenuation budget, so it
    /// multiplies on top of the channel's CC7/CC11 volume rather than replacing it. Default 0 (×1.0).</summary>
    public double GainDb { get; set; }

    /// <summary>Pan <b>offset</b> in -1..+1 added to the channel/zone pan (neutral 0 = leave pan untouched;
    /// NOT an absolute equal-power re-pan). Default 0.</summary>
    public double Pan { get; set; }

    /// <summary>Mute: silences this instrument's voices while held. Default false.</summary>
    public bool Mute { get; set; }

    /// <summary>Solo: when any instrument in the mix is soloed, every non-soloed instrument is silenced.
    /// Default false.</summary>
    public bool Solo { get; set; }

    /// <summary>Additive reverb send (0..1) layered on top of the voice's existing send. Default 0.</summary>
    public double ReverbSend { get; set; }

    /// <summary>Additive chorus send (0..1) layered on top of the voice's existing send. Default 0.</summary>
    public double ChorusSend { get; set; }

    /// <summary>True when every field is at its no-op default — used to keep an untouched entry off the
    /// hot path so its voices stay bit-identical to the pre-mixer engine.</summary>
    public bool IsIdentity =>
        GainDb == 0.0 && Pan == 0.0 && !Mute && !Solo && ReverbSend == 0.0 && ChorusSend == 0.0;
}
