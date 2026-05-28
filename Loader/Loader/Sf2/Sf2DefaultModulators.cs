namespace MidiSharp.SoundBank.Sf2;

/// <summary>
/// The 10 default modulators every SF2 zone implicitly carries, expressed as
/// IR <see cref="ModulationRoute"/>s. See SF2 spec §8.4 for the canonical
/// definition; the route shapes here are the IR translation.
/// </summary>
/// <remarks>
/// These are emitted by <see cref="Sf2ZoneTranslator"/> for every zone unless a
/// zone's explicit modulators override them (SF2 §9.5.4: a custom modulator
/// with the same source+dest+amount-source+transform suppresses the default).
/// Step 2 of the genericization plan emits the full default set; explicit
/// custom-modulator override is deferred to a later step since most SF2 banks
/// don't define any.
/// </remarks>
internal static class Sf2DefaultModulators
{
    public static readonly ModulationRoute[] All =
    {
        // #1: NoteOnVelocity → InitialAttenuation, 960 cB, concave-unipolar-negative.
        // The bedrock of SF2 dynamic range: lower velocity → more attenuation.
        new ModulationRoute
        {
            Source = new ModSource.Velocity(),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        },

        // #2: NoteOnVelocity → InitialFilterFc, -2400 cents, concave-unipolar-negative.
        // Lower velocity → lower cutoff. Universally applied tone-shaping.
        new ModulationRoute
        {
            Source = new ModSource.Velocity(),
            Dest = ModDestination.FilterCutoffCents,
            Amount = -2400.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        },

        // #3: ChannelPressure → Vibrato LFO pitch depth, 50 cents, linear.
        new ModulationRoute
        {
            Source = new ModSource.ChannelPressure(),
            Dest = ModDestination.VibratoLfoPitchDepthCents,
            Amount = 50.0,
            Transform = ModTransform.Linear,
        },

        // #4: CC1 (Mod Wheel) → Vibrato LFO pitch depth, 50 cents, linear.
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(1),
            Dest = ModDestination.VibratoLfoPitchDepthCents,
            Amount = 50.0,
            Transform = ModTransform.Linear,
        },

        // #5: CC7 (Volume) → InitialAttenuation, 960 cB, concave-unipolar-negative.
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(7),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        },

        // #6: CC10 (Pan) → Pan position, ±0.5 (SF2 raw 500 = ±50%), linear bipolar.
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(10),
            Dest = ModDestination.PanNormalized,
            Amount = 0.5,
            Transform = ModTransform.LinearBipolar,
        },

        // #7: CC11 (Expression) → InitialAttenuation, 960 cB, concave-unipolar-negative.
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(11),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        },

        // #8: CC91 (Reverb send) → ReverbSendAmount, 0.2 (SF2 raw 200).
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(91),
            Dest = ModDestination.ReverbSendAmount,
            Amount = 0.2,
            Transform = ModTransform.Linear,
        },

        // #9: CC93 (Chorus send) → ChorusSendAmount, 0.2 (SF2 raw 200).
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(93),
            Dest = ModDestination.ChorusSendAmount,
            Amount = 0.2,
            Transform = ModTransform.Linear,
        },

        // #10: PitchWheel × PitchWheelSensitivity → Pitch, 12700 cents.
        // Compound modulator: bend value scaled by per-channel bend range (RPN 0,0).
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
