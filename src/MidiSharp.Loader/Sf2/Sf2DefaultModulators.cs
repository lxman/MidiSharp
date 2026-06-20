using MidiSharp.SoundBank;

namespace MidiSharp.Loader.Sf2;

/// <summary>
/// The 10 default modulators every SF2 zone implicitly carries (SF2 spec §8.4),
/// each paired with its canonical 16-bit operators so a zone's explicit
/// modulators can override (instrument level) or sum onto (preset level) the
/// matching default by identity. See <see cref="Sf2ModulatorTranslator"/> for
/// the combination rules.
/// </summary>
internal static class Sf2DefaultModulators
{
    /// <summary>
    /// A default modulator: its IR <see cref="ModulationRoute"/> plus the SF2
    /// operator quadruple that defines its identity (SF2 §9.5.1) for override /
    /// additive matching against a zone's explicit modulators.
    /// </summary>
    internal readonly struct DefaultModulator(
        ushort srcOp,
        ushort destOp,
        ushort amtSrcOp,
        ushort transOp,
        ModulationRoute route)
    {
        public readonly ushort SrcOp = srcOp;
        public readonly ushort DestOp = destOp;
        public readonly ushort AmtSrcOp = amtSrcOp;
        public readonly ushort TransOp = transOp;
        public readonly ModulationRoute Route = route;

        public ulong Identity => Sf2ModulatorTranslator.Identity(SrcOp, DestOp, AmtSrcOp, TransOp);
    }

    // Destination operators are the SFGenerator numbers; source operators are the
    // packed 16-bit fields (type|P|D|CC|index, SF2 §8.2).
    private const ushort DestAttenuation = 48;   // InitialAttenuation
    private const ushort DestFilterFc = 8;       // InitialFilterFc
    private const ushort DestVibLfoToPitch = 6;
    private const ushort DestPan = 17;
    private const ushort DestReverbSend = 16;    // ReverbEffectsSend
    private const ushort DestChorusSend = 15;    // ChorusEffectsSend
    private const ushort DestPitch = 59;         // synthetic GEN_PITCH (FluidSynth convention)

    public static readonly DefaultModulator[] Defaults =
    [
        // #1: NoteOnVelocity → InitialAttenuation, 960 cB, concave-unipolar-negative.
        // The bedrock of SF2 dynamic range: lower velocity → more attenuation.
        new DefaultModulator(0x0502, DestAttenuation, 0x0000, 0, new ModulationRoute
        {
            Source = new ModSource.Velocity(),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        }),

        // #2: NoteOnVelocity → InitialFilterFc, -2400 cents, negative.
        // Lower velocity → lower cutoff. Universally applied tone-shaping.
        new DefaultModulator(0x0102, DestFilterFc, 0x0000, 0, new ModulationRoute
        {
            Source = new ModSource.Velocity(),
            Dest = ModDestination.FilterCutoffCents,
            Amount = -2400.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        }),

        // #3: ChannelPressure → Vibrato LFO pitch depth, 50 cents, linear.
        new DefaultModulator(0x000D, DestVibLfoToPitch, 0x0000, 0, new ModulationRoute
        {
            Source = new ModSource.ChannelPressure(),
            Dest = ModDestination.VibratoLfoPitchDepthCents,
            Amount = 50.0,
            Transform = ModTransform.Linear,
        }),

        // #4: CC1 (Mod Wheel) → Vibrato LFO pitch depth, 50 cents, linear.
        new DefaultModulator(0x0081, DestVibLfoToPitch, 0x0000, 0, new ModulationRoute
        {
            Source = new ModSource.ChannelController(1),
            Dest = ModDestination.VibratoLfoPitchDepthCents,
            Amount = 50.0,
            Transform = ModTransform.Linear,
        }),

        // #5: CC7 (Volume) → InitialAttenuation, 960 cB, concave-unipolar-negative.
        new DefaultModulator(0x0587, DestAttenuation, 0x0000, 0, new ModulationRoute
        {
            Source = new ModSource.ChannelController(7),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        }),

        // #6: CC10 (Pan) → Pan position, full ±1.0. The SF2 default-modulator amount is 1000 (= ±100%),
        // and GM/RP-015 puts CC10=0 hard left / 127 hard right — so the pan must span the full image,
        // not the half (±0.5) it used to. Linear bipolar.
        new DefaultModulator(0x028A, DestPan, 0x0000, 0, new ModulationRoute
        {
            Source = new ModSource.ChannelController(10),
            Dest = ModDestination.PanNormalized,
            Amount = 1.0,
            Transform = ModTransform.LinearBipolar,
        }),

        // #7: CC11 (Expression) → InitialAttenuation, 960 cB, concave-unipolar-negative.
        new DefaultModulator(0x058B, DestAttenuation, 0x0000, 0, new ModulationRoute
        {
            Source = new ModSource.ChannelController(11),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        }),

        // #8: CC91 (Reverb send) → ReverbSendAmount, 0.2 (SF2 raw 200).
        new DefaultModulator(0x00DB, DestReverbSend, 0x0000, 0, new ModulationRoute
        {
            Source = new ModSource.ChannelController(91),
            Dest = ModDestination.ReverbSendAmount,
            Amount = 0.2,
            Transform = ModTransform.Linear,
        }),

        // #9: CC93 (Chorus send) → ChorusSendAmount, 0.2 (SF2 raw 200).
        new DefaultModulator(0x00DD, DestChorusSend, 0x0000, 0, new ModulationRoute
        {
            Source = new ModSource.ChannelController(93),
            Dest = ModDestination.ChorusSendAmount,
            Amount = 0.2,
            Transform = ModTransform.Linear,
        }),

        // #10: PitchWheel × PitchWheelSensitivity → Pitch, 12700 cents.
        // Compound modulator: bend value scaled by per-channel bend range (RPN 0,0).
        new DefaultModulator(0x020E, DestPitch, 0x0010, 0, new ModulationRoute
        {
            Source = new ModSource.PitchBend(),
            Dest = ModDestination.PitchCents,
            Amount = 12700.0,
            Transform = ModTransform.Linear,
            AmountModulator = new ModSource.RpnValue(0, 0),
        })
    ];

    /// <summary>
    /// The 10 default routes alone — used directly by zones that declare no
    /// explicit modulators (the common case).
    /// </summary>
    public static readonly ModulationRoute[] All = BuildAll();

    private static ModulationRoute[] BuildAll()
    {
        var routes = new ModulationRoute[Defaults.Length];
        for (var i = 0; i < Defaults.Length; i++)
            routes[i] = Defaults[i].Route;
        return routes;
    }
}
