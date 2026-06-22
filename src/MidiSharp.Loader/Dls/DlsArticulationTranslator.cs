using MidiSharp.SoundBank;

namespace MidiSharp.Loader.Dls;

/// <summary>
/// Converts a DLS articulator <see cref="ConnectionBlock"/> whose source is an
/// external MIDI controller / velocity / pressure / pitch wheel into an IR
/// <see cref="ModulationRoute"/>. Connections whose source is None or an
/// internal modulator (LFO/Vibrato/Eg) are handled by
/// <see cref="DlsZoneBuilder"/> instead — they become static zone parameters
/// or internal-modulator depths rather than runtime routes.
/// </summary>
internal static class DlsArticulationTranslator
{
    /// <summary>
    /// Builds a route from one external-source connection, or returns null if
    /// the (source, destination) pair has no IR representation.
    /// </summary>
    public static ModulationRoute? MakeRoute(ConnectionBlock c)
    {
        ModSource? source = MapSource(c.Source);
        ModDestination? dest = MapDestination(c.Destination);
        if (source == null || dest == null) return null;

        return new ModulationRoute
        {
            Source = source,
            Dest = dest.Value,
            Amount = ScaleToAmount(c.Destination, c.Scale),
            Transform = MapTransform(c.SourceCurve, c.SourceBipolar, c.SourceInverted),
        };
    }

    private static ModSource? MapSource(ConnectionSource s) => s switch
    {
        ConnectionSource.KeyOnVelocity => new ModSource.Velocity(),
        ConnectionSource.KeyNumber => new ModSource.KeyNumber(),
        ConnectionSource.ChannelPressure => new ModSource.ChannelPressure(),
        ConnectionSource.PolyPressure => new ModSource.PolyPressure(),
        ConnectionSource.PitchWheel => new ModSource.PitchBend(),
        ConnectionSource.Cc1 => new ModSource.ChannelController(1),
        ConnectionSource.Cc7 => new ModSource.ChannelController(7),
        ConnectionSource.Cc10 => new ModSource.ChannelController(10),
        ConnectionSource.Cc11 => new ModSource.ChannelController(11),
        ConnectionSource.Cc91 => new ModSource.ChannelController(91),
        ConnectionSource.Cc93 => new ModSource.ChannelController(93),
        ConnectionSource.RpnPitchBendRange => new ModSource.RpnValue(0, 0),
        ConnectionSource.RpnFineTune => new ModSource.RpnValue(0, 1),
        ConnectionSource.RpnCoarseTune => new ModSource.RpnValue(0, 2),
        _ => null,
    };

    private static ModDestination? MapDestination(ConnectionDestination d) => d switch
    {
        ConnectionDestination.Gain => ModDestination.AttenuationDb,
        ConnectionDestination.Pitch => ModDestination.PitchCents,
        ConnectionDestination.Pan => ModDestination.PanNormalized,
        ConnectionDestination.FilterCutoff => ModDestination.FilterCutoffCents,
        ConnectionDestination.FilterQ => ModDestination.FilterResonanceDb,
        ConnectionDestination.LeftReverb => ModDestination.ReverbSendAmount,
        ConnectionDestination.RightReverb => ModDestination.ReverbSendAmount,
        ConnectionDestination.LeftChorus => ModDestination.ChorusSendAmount,
        ConnectionDestination.RightChorus => ModDestination.ChorusSendAmount,
        _ => null,
    };

    private static ModTransform MapTransform(ConnectionTransform curve, bool bipolar, bool inverted)
    {
        return curve switch
        {
            ConnectionTransform.Linear => bipolar ? ModTransform.LinearBipolar : ModTransform.Linear,
            ConnectionTransform.Concave => inverted
                ? ModTransform.ConcaveUnipolarNegative
                : ModTransform.ConcaveUnipolar,
            ConnectionTransform.Convex => ModTransform.ConvexUnipolar,
            ConnectionTransform.Switch => ModTransform.Switch,
            _ => ModTransform.Linear,
        };
    }

    private static double ScaleToAmount(ConnectionDestination dest, int scale)
    {
        double raw = scale / 65536.0;
        return dest switch
        {
            // DLS Gain: positive = louder. IR AttenuationDb: positive = quieter.
            // Flip the sign so a DLS connection that says "vel↓ → -960 cB gain"
            // contributes positive attenuation at low velocity (= quieter), which
            // is the audibly-correct direction.
            ConnectionDestination.Gain => -raw / 10.0,
            ConnectionDestination.FilterQ => raw / 10.0,      // cB → dB (sign matches IR)
            ConnectionDestination.Pan => raw / 500.0,         // 0.1% → fraction
            _ => raw,                                          // cents stay cents
        };
    }
}
