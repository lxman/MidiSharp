using MidiSharp.SoundBank;

namespace Loader.Dls;

/// <summary>
/// The default-articulation set every DLS Level 2 voice carries implicitly,
/// per DLS Level 2 §1.7.10. Banks can override individual connections in
/// their own articulator lists; what survives is what neither
/// instrument-level nor region-level articulators replaced.
/// </summary>
/// <remarks>
/// Concretely these are the velocity-curve / CC-mapping conventions that all
/// DLS-compliant synths apply unless told otherwise. Without them a bank that
/// omits velocity → gain entirely (which the Vintage Dreams DLS does) has no
/// dynamic range at all and renders every note at full level. The set mirrors
/// SF2's default modulator list closely — DLS and SF2 were standardized
/// around the same GM expectations.
/// </remarks>
internal static class DlsDefaultArticulation
{
    /// <summary>
    /// Default routes for external MIDI sources. Emitted by
    /// <see cref="DlsBankLoader"/> for every zone before bank-supplied
    /// articulators run, so bank-supplied connections with the same
    /// (source, dest) will append rather than replace — for an additive
    /// route model this is functionally an override (the bank's value wins
    /// once it routes the same dimension).
    /// </summary>
    public static readonly ModulationRoute[] DefaultRoutes =
    {
        // KeyOnVelocity → Gain @ 96 dB concave-unipolar-negative.
        // Voice attenuates more at low velocity; matches SF2 default #1.
        new ModulationRoute
        {
            Source = new ModSource.Velocity(),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        },

        // ChannelPressure → vibrato pitch depth @ 50 cents linear.
        new ModulationRoute
        {
            Source = new ModSource.ChannelPressure(),
            Dest = ModDestination.VibratoLfoPitchDepthCents,
            Amount = 50.0,
            Transform = ModTransform.Linear,
        },

        // CC1 (Mod Wheel) → vibrato pitch depth @ 50 cents linear.
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(1),
            Dest = ModDestination.VibratoLfoPitchDepthCents,
            Amount = 50.0,
            Transform = ModTransform.Linear,
        },

        // CC7 (Volume) → Gain @ 96 dB concave-unipolar-negative.
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(7),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        },

        // CC10 (Pan) → Pan @ ±0.5 linear bipolar (DLS uses 0.1% ±500 = full ±50%).
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(10),
            Dest = ModDestination.PanNormalized,
            Amount = 1.0,
            Transform = ModTransform.LinearBipolar,
        },

        // CC11 (Expression) → Gain @ 96 dB concave-unipolar-negative.
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(11),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        },

        // CC91 (Reverb send) — DLS uses 100% at full CC (vs SF2's 20%); the
        // synth clamps to 1.0 after summing zone send + route contribution.
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(91),
            Dest = ModDestination.ReverbSendAmount,
            Amount = 1.0,
            Transform = ModTransform.Linear,
        },

        // CC93 (Chorus send) — same.
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(93),
            Dest = ModDestination.ChorusSendAmount,
            Amount = 1.0,
            Transform = ModTransform.Linear,
        },

        // PitchWheel × RpnPitchBendRange → Pitch @ 12700 cents.
        new ModulationRoute
        {
            Source = new ModSource.PitchBend(),
            Dest = ModDestination.PitchCents,
            Amount = 12700.0,
            Transform = ModTransform.Linear,
            AmountModulator = new ModSource.RpnValue(0, 0),
        },
    };
}
