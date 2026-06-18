namespace MidiSharp.SoundBank;

/// <summary>
/// Filter type. SF2 only specifies <see cref="LowPass"/>; SFZ adds the rest;
/// DLS Level 2 has low-pass. Loaders default to LowPass when the source format
/// doesn't differentiate.
/// </summary>
public enum FilterType
{
    LowPass,
    HighPass,
    BandPass,
    LowShelf,
    HighShelf,
    Notch,
}

/// <summary>
/// Per-zone filter configuration. <see cref="PatchZone.Filter"/> being null means
/// no filter — the sample passes through untouched. Non-null means the filter is
/// active with the given type and modulation routing.
/// </summary>
/// <remarks>
/// <see cref="VelocityToCutoffCents"/> exists as a fast path because SF2's default
/// modulator #2 (velocity → filter cutoff at amount -2400) is so universally
/// applied. Loaders still emit it as a route for uniformity; this field saves
/// the synth from a route lookup on every NoteOn for the common case.
/// </remarks>
public sealed class FilterSettings
{
    public FilterType Type { get; init; }

    /// <summary>Cutoff frequency in Hz.</summary>
    public double CutoffHz { get; init; }

    /// <summary>Resonance in dB (0 = none; typical max ~24).</summary>
    public double ResonanceDb { get; init; }

    /// <summary>
    /// Key-tracking amount in cents per key. 0 = no tracking; 100 = full
    /// (cutoff rises one octave per octave of key).
    /// </summary>
    public double KeyTrackCentsPerKey { get; init; }

    /// <summary>
    /// Reference key for <see cref="KeyTrackCentsPerKey"/> (SFZ fil_keycenter). The cutoff shift is
    /// <c>KeyTrackCentsPerKey × (noteKey − KeyTrackCenter)</c>. Defaults to 60 (middle C).
    /// </summary>
    public int KeyTrackCenter { get; init; } = 60;

    /// <summary>SF2 default-modulator fast path. -2400 is the SF2 default.</summary>
    public double VelocityToCutoffCents { get; init; }

    /// <summary>Modulation envelope → cutoff depth, in cents.</summary>
    public double EnvelopeDepthCents { get; init; }

    /// <summary>Modulation LFO → cutoff depth, in cents.</summary>
    public double LfoDepthCents { get; init; }
}

/// <summary>
/// One peaking (bell) EQ band — SFZ <c>eqN_freq</c>/<c>eqN_bw</c>/<c>eqN_gain</c>. A zone carries a
/// list of these (<see cref="PatchZone.EqBands"/>); the synth runs each as a biquad on the voice signal.
/// </summary>
public readonly struct EqBand
{
    /// <summary>Centre frequency in Hz.</summary>
    public double FrequencyHz { get; init; }

    /// <summary>Bandwidth in octaves (SFZ default 1.0).</summary>
    public double BandwidthOctaves { get; init; }

    /// <summary>Peak gain in dB (positive boosts, negative cuts; 0 = inactive).</summary>
    public double GainDb { get; init; }

    /// <summary>The SFZ band number N (from <c>eqN_*</c>), so an LFO target (lfoN_eqNgain/freq) can
    /// address this band. 0 when unspecified (SF2/DLS, which don't number EQ bands).</summary>
    public int BandNumber { get; init; }
}
