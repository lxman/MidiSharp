using MidiSharp.SoundBank;

namespace MidiSharp.Loader.Sfz;

/// <summary>
/// Default MIDI-controller routes applied to every SFZ zone. SFZ — unlike SF2
/// and DLS — defines no implicit controller behavior; a bare region ignores
/// volume, expression, pan, and pitch-bend entirely. Real SFZ players supply
/// these GM-standard responses so MIDI files play musically, so we do too.
/// </summary>
/// <remarks>
/// Velocity → amplitude is intentionally NOT here — that's per-region, driven
/// by <c>amp_veltrack</c>, and is emitted by <see cref="SfzZoneTranslator"/>.
/// </remarks>
internal static class SfzDefaultRoutes
{
    public static readonly ModulationRoute[] Routes =
    [
        // CC7 Channel Volume → attenuation (96 dB concave, quieter as CC falls).
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(7),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        },
        // CC11 Expression → attenuation (same curve).
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(11),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        },
        // CC10 Pan → pan (±full at CC 0/127, center at 64).
        new ModulationRoute
        {
            Source = new ModSource.ChannelController(10),
            Dest = ModDestination.PanNormalized,
            Amount = 1.0,
            Transform = ModTransform.LinearBipolar,
        }
        // Pitch wheel is NOT here — SFZ scales it by the region's bend_up/bend_down (not the channel
        // RPN range), so SfzZoneTranslator emits a per-zone pitch-bend route instead.
    ];
}
