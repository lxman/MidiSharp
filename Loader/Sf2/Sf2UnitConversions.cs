using System;

namespace Loader.Sf2;

/// <summary>
/// Conversions between SF2's signed-short encodings and the IR's domain-natural
/// units (seconds, Hz, dB, linear amplitude). These conversions are the
/// boundary at which SF2's wire format meets the format-neutral IR — nothing
/// outside this directory should need to know what "timecents" are.
/// </summary>
internal static class Sf2UnitConversions
{
    /// <summary>
    /// EMU's de-facto attenuation scaling factor. Not in the SF2 spec, but
    /// every shipping renderer (FluidSynth, TinySoundFont, EMU's own engines)
    /// applies it to InitialAttenuation. Without it, banks sound 2-3× too
    /// loud relative to commercial GM expectations.
    /// </summary>
    public const double EmuAttenuationFactor = 0.4;

    /// <summary>
    /// SF2 timecents → seconds. Per spec §8.1.3, values ≤ -12000 (1 ms) are
    /// the "instantaneous" sentinel and produce 0 seconds (the synth skips
    /// the phase entirely).
    /// </summary>
    public static double TimecentsToSeconds(short timecents)
    {
        if (timecents <= -12000) return 0.0;
        return Math.Pow(2.0, timecents / 1200.0);
    }

    /// <summary>
    /// SF2 absolute cents → Hz, using the 8.176 Hz reference (= MIDI key 0).
    /// Used for filter cutoff and LFO frequency.
    /// </summary>
    public static double AbsoluteCentsToHz(short cents)
        => 8.176 * Math.Pow(2.0, cents / 1200.0);

    /// <summary>Centibels (0.1 dB units) → dB.</summary>
    public static double CentibelsToDb(short centibels) => centibels / 10.0;

    /// <summary>
    /// SF2 sustain encoding (centibels of attenuation below peak) → 0..1
    /// linear amplitude multiplier. SustainVolEnv of 0 cB = full sustain (1.0);
    /// 1000 cB = -100 dB ≈ 1e-5.
    /// </summary>
    public static double SustainCentibelsToLinear(short centibels)
    {
        if (centibels <= 0) return 1.0;
        return Math.Pow(10.0, -centibels / 200.0);
    }

    /// <summary>
    /// SF2 0.1%-units → 0..1 fraction. Used for effect sends (CC91/CC93
    /// equivalent generators) and pan (where ±500 = ±50%, encoded as ±0.5).
    /// </summary>
    public static double TenthOfPercentToFraction(short value) => value / 1000.0;
}
