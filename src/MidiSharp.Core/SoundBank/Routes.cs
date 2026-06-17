namespace MidiSharp.SoundBank;

/// <summary>
/// A modulation source. Records-with-payload (e.g.
/// <see cref="ChannelController"/>(1) for the mod wheel) keep the model open
/// without growing a 128-entry enum. The synth pattern-matches in its per-block
/// route evaluator.
/// </summary>
public abstract record ModSource
{
    /// <summary>NoteOn velocity (0..127).</summary>
    public sealed record Velocity : ModSource;

    /// <summary>The played key number (0..127).</summary>
    public sealed record KeyNumber : ModSource;

    /// <summary>Channel pressure / aftertouch (0..127).</summary>
    public sealed record ChannelPressure : ModSource;

    /// <summary>Polyphonic key pressure (0..127).</summary>
    public sealed record PolyPressure : ModSource;

    /// <summary>14-bit pitch bend (-8192..+8191, centered at 0).</summary>
    public sealed record PitchBend : ModSource;

    /// <summary>Any MIDI continuous controller (0..127).</summary>
    public sealed record ChannelController(byte Number) : ModSource;

    /// <summary>Registered Parameter Number, addressed by (MSB, LSB).</summary>
    public sealed record RpnValue(byte Msb, byte Lsb) : ModSource;

    /// <summary>
    /// SF2 zero-source-id case: a modulator whose source side is unconnected.
    /// Evaluates to 0 (so the route contributes nothing).
    /// </summary>
    public sealed record NoConnection : ModSource;
}

/// <summary>
/// A modulation destination. The unit suffix (Cents/Db/Normalized) is part of
/// the name so <see cref="ModulationRoute.Amount"/> is self-documenting.
/// </summary>
public enum ModDestination
{
    /// <summary>±cents added to playback pitch.</summary>
    PitchCents,

    /// <summary>±cents added to filter cutoff frequency.</summary>
    FilterCutoffCents,

    /// <summary>±dB added to filter resonance.</summary>
    FilterResonanceDb,

    /// <summary>±dB added to voice attenuation (negative = louder).</summary>
    AttenuationDb,

    /// <summary>±value added to pan (-1..+1).</summary>
    PanNormalized,

    /// <summary>Adds to <see cref="LFOSettings.PitchDepthCents"/> on the vibrato LFO.</summary>
    VibratoLfoPitchDepthCents,

    /// <summary>Adds to <see cref="LFOSettings.PitchDepthCents"/> on the modulation LFO.</summary>
    ModulationLfoPitchDepthCents,

    /// <summary>Adds to <see cref="LFOSettings.VolumeDepthDb"/> on the modulation LFO.</summary>
    ModulationLfoVolumeDepthDb,

    /// <summary>Adds to <see cref="LFOSettings.FilterDepthCents"/> on the modulation LFO.</summary>
    ModulationLfoFilterDepthCents,

    /// <summary>Adds to <see cref="FilterSettings.EnvelopeDepthCents"/>.</summary>
    ModulationEnvelopeToFilterCents,

    /// <summary>Mod-envelope contribution to pitch, in cents.</summary>
    ModulationEnvelopeToPitchCents,

    /// <summary>Adds to <see cref="PatchZone.ReverbSend"/> (clamped 0..1 after summing).</summary>
    ReverbSendAmount,

    /// <summary>Adds to <see cref="PatchZone.ChorusSend"/> (clamped 0..1 after summing).</summary>
    ChorusSendAmount,
}

/// <summary>
/// Curve applied to a normalized source value before scaling by
/// <see cref="ModulationRoute.Amount"/>. SF2's curve set is the canonical
/// reference (10 curves total); other formats either match a subset or
/// decompose to these.
/// </summary>
public enum ModTransform
{
    /// <summary>y = x.</summary>
    Linear,

    /// <summary>SF2 fig E: log-shaped, quick rise then asymptotic to 1.</summary>
    ConcaveUnipolar,

    /// <summary>SF2 fig F: slow rise then quick approach to 1.</summary>
    ConvexUnipolar,

    /// <summary>SF2 fig G: 0 below 0.5, 1 above.</summary>
    Switch,

    /// <summary>-1..+1 bipolar (centered at 0.5 input).</summary>
    LinearBipolar,

    /// <summary>
    /// Concave-unipolar inverted: used for velocity → attenuation
    /// (high velocity = no attenuation; low velocity = much attenuation).
    /// </summary>
    ConcaveUnipolarNegative,

    /// <summary>
    /// SFZ amplitude_oncc: the source maps to a LINEAR gain via the route's ARIA curve
    /// (<see cref="ModulationRoute.CurveIndex"/>), scaled by Amount (depth fraction), then converted to
    /// the AttenuationDb destination as −20·log10(gain). Lets amplitude_curvecc shape the response.
    /// </summary>
    AmplitudeCurve,
}

/// <summary>
/// One modulation routing from a source through a transform/scaler to a
/// destination. Optionally the amount itself can be modulated by a second
/// source (SF2's secondary-source modulator slot).
/// </summary>
public sealed class ModulationRoute
{
    public ModSource Source { get; init; } = new ModSource.NoConnection();

    public ModDestination Dest { get; init; }

    /// <summary>Signed scaling. Units are determined by <see cref="Dest"/>.</summary>
    public double Amount { get; init; }

    public ModTransform Transform { get; init; } = ModTransform.Linear;

    /// <summary>ARIA curve index for <see cref="ModTransform.AmplitudeCurve"/> (SFZ amplitude_curvecc). 0 = linear.</summary>
    public int CurveIndex { get; init; }

    /// <summary>
    /// Optional secondary source whose normalized value scales
    /// <see cref="Amount"/> per-block. Null = static amount.
    /// </summary>
    public ModSource? AmountModulator { get; init; }
}
