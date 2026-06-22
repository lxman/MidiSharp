using System;
using System.Collections.Generic;
using MidiSharp.SoundBank;

namespace MidiSharp.Synth;

/// <summary>
/// Per-block accumulated contribution from a zone's modulation routes to each
/// destination. Value type so per-voice/per-block evaluation doesn't allocate.
/// </summary>
/// <remarks>
/// Routes always *add* to the zone's static base value — they never replace it.
/// Voice initializes its base values from the <see cref="PatchZone"/> at
/// Configure, then in each Process block evaluates routes against the current
/// channel state and applies the accumulated contributions.
/// </remarks>
internal struct RouteContributions
{
    public double PitchCents;
    public double FilterCutoffCents;
    public double FilterResonanceDb;
    public double AttenuationDb;
    public double PanNormalized;
    public double VibratoLfoPitchDepthCents;
    public double ModulationLfoPitchDepthCents;
    public double ModulationLfoVolumeDepthDb;
    public double ModulationLfoFilterDepthCents;
    public double ModulationEnvelopeToFilterCents;
    public double ModulationEnvelopeToPitchCents;
    public double ReverbSendAmount;
    public double ChorusSendAmount;
}

/// <summary>
/// Walks a zone's <see cref="ModulationRoute"/> list once per block, evaluates
/// each source against the current channel/voice state, applies the curve
/// transform, scales by the route's amount, and sums into the destination
/// buckets in a <see cref="RouteContributions"/> struct.
/// </summary>
/// <remarks>
/// This is the centerpiece of the format-neutral synth. Every SF2 default
/// modulator becomes one row in the zone's <c>Routes</c> list at load time,
/// so the synth itself contains no SF2-specific behavior — change the route
/// data (e.g., a future SFZ loader emitting CC-mapped routings) and the same
/// evaluator produces the right contributions without any code change.
/// </remarks>
internal static class RouteEvaluator
{
    public static void Evaluate(
        IReadOnlyList<ModulationRoute> routes,
        int velocity,
        int keyNumber,
        byte polyPressure,
        ChannelState channelState,
        out RouteContributions contributions)
    {
        contributions = default;
        if (routes == null) return;

        int count = routes.Count;
        for (var i = 0; i < count; i++)
        {
            ModulationRoute? route = routes[i];

            double srcRaw = EvaluateSource(route.Source, velocity, keyNumber, polyPressure, channelState);

            // SFZ amplitude_oncc: source → linear gain via the ARIA curve, scaled by depth (Amount),
            // then to attenuation dB. Kept separate from the generic transform×amount path because the
            // gain→dB conversion is nonlinear.
            if (route.Transform == ModTransform.AmplitudeCurve)
            {
                double shaped = route.CurveTable is { } ampTable ? CurveLookup(ampTable, srcRaw)
                                                                 : AriaCurve.Eval(route.CurveIndex, srcRaw);
                double gain = route.Amount * shaped;
                double att = -20.0 * Math.Log10(Math.Clamp(gain, 1e-5, 1e5));
                contributions.AttenuationDb += att;
                continue;
            }

            // A resolved CC-response curve (SFZ *_curvecc) maps the source instead of the linear transform.
            double transformed = route.CurveTable is { } table
                ? CurveLookup(table, srcRaw)
                : ApplyTransform(srcRaw, route.Transform);
            double amount = route.Amount;

            // SF2 modulator #10 (pitch wheel × bend range) is the canonical
            // amount-modulator case: the static amount (12700 cents) is scaled
            // by the per-channel bend range, so per-channel RPN changes affect
            // pitch-bend depth without per-voice re-evaluation.
            if (route.AmountModulator != null)
            {
                double amtRaw = EvaluateSource(route.AmountModulator, velocity, keyNumber, polyPressure, channelState);
                amount *= amtRaw;
            }

            double contribution = transformed * amount;

            switch (route.Dest)
            {
                case ModDestination.PitchCents: contributions.PitchCents += contribution; break;
                case ModDestination.FilterCutoffCents: contributions.FilterCutoffCents += contribution; break;
                case ModDestination.FilterResonanceDb: contributions.FilterResonanceDb += contribution; break;
                case ModDestination.AttenuationDb: contributions.AttenuationDb += contribution; break;
                case ModDestination.PanNormalized: contributions.PanNormalized += contribution; break;
                case ModDestination.VibratoLfoPitchDepthCents: contributions.VibratoLfoPitchDepthCents += contribution; break;
                case ModDestination.ModulationLfoPitchDepthCents: contributions.ModulationLfoPitchDepthCents += contribution; break;
                case ModDestination.ModulationLfoVolumeDepthDb: contributions.ModulationLfoVolumeDepthDb += contribution; break;
                case ModDestination.ModulationLfoFilterDepthCents: contributions.ModulationLfoFilterDepthCents += contribution; break;
                case ModDestination.ModulationEnvelopeToFilterCents: contributions.ModulationEnvelopeToFilterCents += contribution; break;
                case ModDestination.ModulationEnvelopeToPitchCents: contributions.ModulationEnvelopeToPitchCents += contribution; break;
                case ModDestination.ReverbSendAmount: contributions.ReverbSendAmount += contribution; break;
                case ModDestination.ChorusSendAmount: contributions.ChorusSendAmount += contribution; break;
            }
        }
    }

    private static double EvaluateSource(
        ModSource src,
        int velocity,
        int keyNumber,
        byte polyPressure,
        ChannelState channelState)
    {
        switch (src)
        {
            case ModSource.Velocity: return velocity / 127.0;
            case ModSource.KeyNumber: return keyNumber / 127.0;
            case ModSource.ChannelPressure: return channelState.ChannelPressure / 127.0;
            case ModSource.PolyPressure: return polyPressure / 127.0;

            // Pitch bend produces a signed -1..+1 directly. Routes against it use
            // Linear transform (treating it as already bipolar).
            case ModSource.PitchBend:
                return (channelState.PitchBend - 8192) / 8192.0;

            case ModSource.ChannelController cc:
                return channelState.GetCC(cc.Number) / 127.0;

            // RPN 0,0 is pitch bend range in semitones. The SF2 default modulator
            // #10 uses this as an amount modulator scaling the 12700-cent base:
            //   contribution = bend × (bendRange / 127) × 12700 cents
            //              = bend × bendRange × 100 cents
            // i.e., bendRange semitones of pitch swing at full bend. Includes the
            // RPN 0,0 fractional cents component when present.
            case ModSource.RpnValue { Msb: 0, Lsb: 0 }:
                return (channelState.PitchBendRange * 100 + channelState.PitchBendRangeCents) / 12700.0;

            case ModSource.NoConnection: return 0.0;

            default: return 0.0;
        }
    }

    /// <summary>Maps a normalized source x∈[0,1] through a resolved 128-entry CC-response curve.</summary>
    private static double CurveLookup(double[] table, double x)
    {
        var i = (int)(Math.Clamp(x, 0.0, 1.0) * 127.0 + 0.5);
        return table[i < 0 ? 0 : i > 127 ? 127 : i];
    }

    private static double ApplyTransform(double x, ModTransform transform)
    {
        switch (transform)
        {
            case ModTransform.Linear: return x;
            case ModTransform.LinearBipolar: return 2.0 * x - 1.0;
            case ModTransform.ConcaveUnipolar: return ConcaveCurve(x);
            case ModTransform.ConvexUnipolar: return 1.0 - ConcaveCurve(1.0 - x);
            case ModTransform.Switch: return x >= 0.5 ? 1.0 : 0.0;
            case ModTransform.ConcaveUnipolarNegative: return ConcaveCurve(1.0 - x);
            default: return x;
        }
    }

    /// <summary>
    /// SF2 concave source curve, per the spec §8.1.4 fig E and FluidSynth's
    /// implementation: <c>y = -20/96 × log10(1 - x)</c>, clamped to [0, 1].
    /// Maps 0 → 0, 1 → 1, with the curve rising very slowly until x approaches
    /// 1, then steeply. The "rises slowly" shape is why the same default
    /// modulator (velocity → 96 dB attenuation) produces only ~6 dB attenuation
    /// at vel=64 instead of ~12 — soft notes are present but not silenced.
    /// </summary>
    private static double ConcaveCurve(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double y = -20.0 / 96.0 * Math.Log10(1.0 - x);
        return y > 1.0 ? 1.0 : y;
    }
}
