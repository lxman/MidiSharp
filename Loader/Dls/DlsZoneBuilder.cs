using System;
using System.Collections.Generic;
using MidiSharp.SoundBank;

namespace Loader.Dls;

/// <summary>
/// Mutable per-zone accumulator that processes DLS articulator connections
/// into IR-shaped settings. Three classes of connection are handled:
/// </summary>
/// <list type="bullet">
/// <item><b>Source = None</b> — sets a static parameter (envelope time/level,
/// filter cutoff, pan, LFO frequency, etc.). The Scale is decoded according
/// to the destination's natural units.</item>
/// <item><b>Source = Lfo / Vibrato / Eg1 / Eg2</b> — these are voice-internal
/// modulators. The Scale becomes the depth of the modulator's action on the
/// destination (e.g., LFO → Gain = tremolo depth in dB).</item>
/// <item><b>Source = MIDI controller / Velocity / Pressure / PitchWheel</b> —
/// proper runtime modulation; emitted as a <see cref="ModulationRoute"/> for
/// the IR's per-block route evaluator.</item>
/// </list>
internal sealed class DlsZoneBuilder
{
    // Envelope state — defaults match the SF2 / DLS "do nothing" baseline.
    public double VolDelaySec, VolAttackSec, VolHoldSec, VolDecaySec, VolReleaseSec;
    public double VolSustainLevel = 1.0;
    public double ModDelaySec, ModAttackSec, ModHoldSec, ModDecaySec, ModReleaseSec;
    public double ModSustainLevel = 1.0;
    public bool HasModEnvelope;

    // LFOs — set HasXxx when any connection populates them.
    public double ModLfoDelaySec, ModLfoFreqHz = 5.0;
    public double ModLfoPitchCents, ModLfoVolumeDb, ModLfoFilterCents;
    public bool HasModLfo;

    public double VibLfoDelaySec, VibLfoFreqHz = 5.0;
    public double VibLfoPitchCents;
    public bool HasVibLfo;

    // Filter
    public double FilterCutoffHz = 20000.0;
    public double FilterResonanceDb;
    public double FilterModEnvCents;   // mod-env → cutoff depth (cents)
    public bool HasFilter;

    // Mod envelope → pitch (lives on PitchSettings in the IR)
    public double PitchModEnvCents;

    // Level / pan / sends
    public double AttenuationDb;
    public double PanNormalized;
    public double ReverbSend;
    public double ChorusSend;

    // Runtime routes for external (MIDI) sources.
    public readonly List<ModulationRoute> Routes = new();

    public void Apply(IReadOnlyList<ConnectionBlock> connections)
    {
        foreach (var c in connections) Apply(c);
    }

    private void Apply(ConnectionBlock c)
    {
        switch (c.Source)
        {
            case ConnectionSource.None:
                ApplyStatic(c);
                break;
            case ConnectionSource.Lfo:
            case ConnectionSource.Vibrato:
            case ConnectionSource.Eg1:
            case ConnectionSource.Eg2:
                ApplyInternalModulator(c);
                break;
            default:
                ApplyExternalRoute(c);
                break;
        }
    }

    // ── Source = None → static parameter setter ─────────────────────

    private void ApplyStatic(ConnectionBlock c)
    {
        switch (c.Destination)
        {
            // Volume envelope timings (time-cents). 0 → 1 s, +1200 → 2 s, etc.
            case ConnectionDestination.Eg1DelayTime: VolDelaySec = TimeCentsToSec(c.Scale); break;
            case ConnectionDestination.Eg1AttackTime: VolAttackSec = TimeCentsToSec(c.Scale); break;
            case ConnectionDestination.Eg1HoldTime: VolHoldSec = TimeCentsToSec(c.Scale); break;
            case ConnectionDestination.Eg1DecayTime: VolDecaySec = TimeCentsToSec(c.Scale); break;
            case ConnectionDestination.Eg1ReleaseTime: VolReleaseSec = TimeCentsToSec(c.Scale); break;
            case ConnectionDestination.Eg1SustainLevel: VolSustainLevel = SustainCbToLinear(c.Scale); break;

            // Modulation envelope — same pattern.
            case ConnectionDestination.Eg2DelayTime: ModDelaySec = TimeCentsToSec(c.Scale); HasModEnvelope = true; break;
            case ConnectionDestination.Eg2AttackTime: ModAttackSec = TimeCentsToSec(c.Scale); HasModEnvelope = true; break;
            case ConnectionDestination.Eg2HoldTime: ModHoldSec = TimeCentsToSec(c.Scale); HasModEnvelope = true; break;
            case ConnectionDestination.Eg2DecayTime: ModDecaySec = TimeCentsToSec(c.Scale); HasModEnvelope = true; break;
            case ConnectionDestination.Eg2ReleaseTime: ModReleaseSec = TimeCentsToSec(c.Scale); HasModEnvelope = true; break;
            case ConnectionDestination.Eg2SustainLevel: ModSustainLevel = SustainCbToLinear(c.Scale); HasModEnvelope = true; break;

            // Modulation LFO timing.
            case ConnectionDestination.LfoFrequency: ModLfoFreqHz = AbsCentsToHz(c.Scale); HasModLfo = true; break;
            case ConnectionDestination.LfoStartDelay: ModLfoDelaySec = TimeCentsToSec(c.Scale); HasModLfo = true; break;

            // Dedicated vibrato LFO timing (DLS Level 2).
            case ConnectionDestination.VibratoFrequency: VibLfoFreqHz = AbsCentsToHz(c.Scale); HasVibLfo = true; break;
            case ConnectionDestination.VibratoStartDelay: VibLfoDelaySec = TimeCentsToSec(c.Scale); HasVibLfo = true; break;

            // Filter.
            case ConnectionDestination.FilterCutoff: FilterCutoffHz = AbsCentsToHz(c.Scale); HasFilter = true; break;
            case ConnectionDestination.FilterQ: FilterResonanceDb = ScaleToDb(c.Scale); HasFilter = true; break;

            // Level / pan. DLS Gain is signed cB of gain (positive = louder,
            // negative = attenuation); the IR's AttenuationDb is non-negative
            // (positive = quieter), so we negate.
            case ConnectionDestination.Gain:
            {
                double db = ScaleToDb(c.Scale);
                AttenuationDb += db <= 0 ? -db : 0;
                break;
            }
            case ConnectionDestination.Pan: PanNormalized = ScaleToPan(c.Scale); break;

            // Effect sends (L/R fold to mono). 0..1 fraction.
            case ConnectionDestination.LeftReverb:
            case ConnectionDestination.RightReverb:
                ReverbSend = Math.Max(ReverbSend, Math.Clamp(c.Scale / 65536.0 / 1000.0, 0, 1));
                break;
            case ConnectionDestination.LeftChorus:
            case ConnectionDestination.RightChorus:
                ChorusSend = Math.Max(ChorusSend, Math.Clamp(c.Scale / 65536.0 / 1000.0, 0, 1));
                break;
        }
    }

    // ── Internal modulator source → depth setter ─────────────────────

    private void ApplyInternalModulator(ConnectionBlock c)
    {
        // Scale is the depth of the modulator's effect on the destination, in
        // the destination's natural units.
        switch (c.Source)
        {
            case ConnectionSource.Lfo:
                HasModLfo = true;
                switch (c.Destination)
                {
                    case ConnectionDestination.Pitch: ModLfoPitchCents = c.Scale / 65536.0; break;
                    case ConnectionDestination.Gain: ModLfoVolumeDb = ScaleToDb(c.Scale); break;
                    case ConnectionDestination.FilterCutoff: ModLfoFilterCents = c.Scale / 65536.0; break;
                }
                break;
            case ConnectionSource.Vibrato:
                HasVibLfo = true;
                if (c.Destination == ConnectionDestination.Pitch)
                    VibLfoPitchCents = c.Scale / 65536.0;
                break;
            case ConnectionSource.Eg1:
                // Eg1 = volume envelope. Mod-envelope-to-volume isn't a
                // standalone destination in our IR (the volume envelope IS
                // the gain) — connections here are degenerate; skip.
                break;
            case ConnectionSource.Eg2:
                HasModEnvelope = true;
                switch (c.Destination)
                {
                    case ConnectionDestination.Pitch: PitchModEnvCents = c.Scale / 65536.0; break;
                    case ConnectionDestination.FilterCutoff: FilterModEnvCents = c.Scale / 65536.0; HasFilter = true; break;
                }
                break;
        }
    }

    // ── External MIDI source → runtime ModulationRoute ──────────────

    private void ApplyExternalRoute(ConnectionBlock c)
    {
        var route = DlsArticulationTranslator.MakeRoute(c);
        if (route != null) Routes.Add(route);
    }

    // ── Scale → physical-unit conversions ───────────────────────────

    /// <summary>
    /// DLS time-cents (Scale / 65536) → seconds. 0 timecents = 1 s; ≤ -12000
    /// is the standard "instantaneous" sentinel (matches SF2 §8.1.3).
    /// </summary>
    public static double TimeCentsToSec(int scale)
    {
        double tc = scale / 65536.0;
        if (tc <= -12000) return 0;
        return Math.Pow(2.0, tc / 1200.0);
    }

    /// <summary>DLS absolute cents → Hz, using the 8.176 Hz reference (= MIDI key 0).</summary>
    public static double AbsCentsToHz(int scale)
    {
        double cents = scale / 65536.0;
        return 8.176 * Math.Pow(2.0, cents / 1200.0);
    }

    /// <summary>DLS centibels (Scale / 65536) → dB.</summary>
    public static double ScaleToDb(int scale) => scale / 65536.0 / 10.0;

    /// <summary>
    /// DLS sustain encoding (centibels of attenuation below peak) → 0..1 linear
    /// amplitude. Matches SF2's SustainVolEnv interpretation.
    /// </summary>
    public static double SustainCbToLinear(int scale)
    {
        double cb = scale / 65536.0;
        if (cb <= 0) return 1.0;
        return Math.Pow(10.0, -cb / 200.0);
    }

    /// <summary>
    /// DLS pan: Scale / 65536 is in 0.1% units (-500..+500 = full L..R), same
    /// convention as SF2. IR Pan is -1..+1, so we divide by 500.
    /// </summary>
    public static double ScaleToPan(int scale) => Math.Clamp(scale / 65536.0 / 500.0, -1.0, 1.0);
}
