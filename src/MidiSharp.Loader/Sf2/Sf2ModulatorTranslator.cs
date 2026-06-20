using System.Collections.Generic;
using MidiSharp.Loader.Sf2.Enums;
using MidiSharp.Loader.Sf2.Model;
using MidiSharp.SoundBank;

namespace MidiSharp.Loader.Sf2;

/// <summary>
/// Translates SF2 modulators (the 16-bit source/dest/transform operator triples,
/// SF2 spec §8.2–8.4) into IR <see cref="ModulationRoute"/>s and combines a zone's
/// explicit modulators with the 10 default modulators.
/// </summary>
/// <remarks>
/// Combination follows the SF2 spec / FluidSynth de-facto semantics (the A/B
/// reference): the default modulators are the base; <b>instrument</b>-level
/// modulators <i>overwrite</i> (replace an identical default/earlier mod, else
/// append); <b>preset</b>-level modulators <i>add</i> (sum the amount into an
/// identical mod, else append). Two modulators are "identical" when their
/// (source, dest, amount-source, transform) operators all match (SF2 §9.5.1).
///
/// This is what brings velocity- and key-tracked filter behavior to life: e.g.
/// a piano whose instrument-global declares <c>velocity → ModEnvToFilterFc</c>
/// at +8500 cents opens its lowpass on hard strikes. Emitting only the 10
/// defaults (the prior behavior) left that filter clamped shut, muffling the
/// high register.
/// </remarks>
internal static class Sf2ModulatorTranslator
{
    /// <summary>
    /// Produce the effective route list for one zone: defaults, then instrument
    /// modulators (overwrite), then preset modulators (add).
    /// </summary>
    public static IReadOnlyList<ModulationRoute> Combine(
        IReadOnlyList<Modulator>? instrumentGlobal,
        IReadOnlyList<Modulator>? instrumentLocal,
        IReadOnlyList<Modulator>? presetGlobal,
        IReadOnlyList<Modulator>? presetLocal)
    {
        // Fast path: no explicit modulators anywhere → the shared default array.
        if (IsEmpty(instrumentGlobal) && IsEmpty(instrumentLocal) &&
            IsEmpty(presetGlobal) && IsEmpty(presetLocal))
        {
            return Sf2DefaultModulators.All;
        }

        var entries = new List<Entry>(Sf2DefaultModulators.Defaults.Length + 8);
        foreach (var d in Sf2DefaultModulators.Defaults)
            entries.Add(new Entry(d.Identity, d.Route));

        // Instrument level: global first, then local — each overwrites on identity.
        ApplyOverwrite(entries, instrumentGlobal);
        ApplyOverwrite(entries, instrumentLocal);

        // Preset level: additive on identity (sums onto defaults/instrument mods).
        ApplyAdd(entries, presetGlobal);
        ApplyAdd(entries, presetLocal);

        var routes = new ModulationRoute[entries.Count];
        for (var i = 0; i < entries.Count; i++)
            routes[i] = entries[i].Route;
        return routes;
    }

    private static bool IsEmpty(IReadOnlyList<Modulator>? mods) => mods == null || mods.Count == 0;

    private readonly struct Entry(ulong identity, ModulationRoute route)
    {
        public readonly ulong Identity = identity;
        public readonly ModulationRoute Route = route;
    }

    private static void ApplyOverwrite(List<Entry> entries, IReadOnlyList<Modulator>? mods)
    {
        if (mods == null) return;
        foreach (var m in mods)
        {
            var route = TryTranslate(m);
            if (route == null) continue;
            var id = IdentityOf(m);
            var idx = IndexOf(entries, id);
            if (idx >= 0) entries[idx] = new Entry(id, route);
            else entries.Add(new Entry(id, route));
        }
    }

    private static void ApplyAdd(List<Entry> entries, IReadOnlyList<Modulator>? mods)
    {
        if (mods == null) return;
        foreach (var m in mods)
        {
            var route = TryTranslate(m);
            if (route == null) continue;
            var id = IdentityOf(m);
            var idx = IndexOf(entries, id);
            if (idx < 0) { entries.Add(new Entry(id, route)); continue; }

            // Identical modulator already present (a default or an instrument mod):
            // sum the amounts. Source/transform are identical by definition.
            var ex = entries[idx].Route;
            entries[idx] = new Entry(id, new ModulationRoute
            {
                Source = ex.Source,
                Dest = ex.Dest,
                Amount = ex.Amount + route.Amount,
                Transform = ex.Transform,
                AmountModulator = ex.AmountModulator,
            });
        }
    }

    private static int IndexOf(List<Entry> entries, ulong id)
    {
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Identity == id) return i;
        return -1;
    }

    internal static ulong IdentityOf(Modulator m) =>
        Identity(m.SourceOperator, (ushort)m.DestinationOperator, m.AmountSourceOperator, m.TransformOperator);

    /// <summary>Pack the four operator fields into a comparable key (SF2 §9.5.1).</summary>
    internal static ulong Identity(ushort src, ushort dest, ushort amtSrc, ushort trans) =>
        ((ulong)src << 48) | ((ulong)dest << 32) | ((ulong)amtSrc << 16) | trans;

    /// <summary>
    /// Translate a single SF2 modulator to an IR route, or null if its source or
    /// destination has no IR representation (e.g. NoController source, or a
    /// release-time destination the synth doesn't expose as a route).
    /// </summary>
    public static ModulationRoute? TryTranslate(Modulator m)
    {
        var source = MapSource(m.SourceOperator);
        if (source == null) return null;
        if (!TryMapDest(m.DestinationOperator, out var dest)) return null;

        return new ModulationRoute
        {
            Source = source,
            Dest = dest,
            Amount = ConvertAmount(dest, m.DestinationOperator, m.Amount),
            Transform = MapTransform(m.SourceOperator),
            AmountModulator = MapAmountSource(m.AmountSourceOperator),
        };
    }

    private static ModSource? MapSource(ushort srcOp)
    {
        var isCc = ((srcOp >> 7) & 0x1) == 1;
        var index = srcOp & 0x7F;

        if (isCc) return new ModSource.ChannelController((byte)index);

        // General controller (SF2 §8.2.1).
        return index switch
        {
            2 => new ModSource.Velocity(),          // NoteOnVelocity
            3 => new ModSource.KeyNumber(),         // NoteOnKeyNum
            10 => new ModSource.PolyPressure(),
            13 => new ModSource.ChannelPressure(),
            14 => new ModSource.PitchBend(),        // PitchWheel
            16 => new ModSource.RpnValue(0, 0),     // PitchWheelSensitivity (RPN 0,0)
            _ => null,                              // 0 = NoController (constant) and unknowns: skip
        };
    }

    /// <summary>
    /// Secondary (amount) source. A zero operator is SF2's "no controller" =
    /// constant 1.0, i.e. a static amount, so we return null (no amount modulator).
    /// </summary>
    private static ModSource? MapAmountSource(ushort amtSrcOp) =>
        amtSrcOp == 0 ? null : MapSource(amtSrcOp);

    private static bool TryMapDest(SFGenerator gen, out ModDestination dest)
    {
        switch (gen)
        {
            case SFGenerator.InitialAttenuation: dest = ModDestination.AttenuationDb; return true;
            case SFGenerator.InitialFilterFc: dest = ModDestination.FilterCutoffCents; return true;
            case SFGenerator.InitialFilterQ: dest = ModDestination.FilterResonanceDb; return true;
            case SFGenerator.ModEnvToFilterFc: dest = ModDestination.ModulationEnvelopeToFilterCents; return true;
            case SFGenerator.ModLfoToFilterFc: dest = ModDestination.ModulationLfoFilterDepthCents; return true;
            case SFGenerator.ModEnvToPitch: dest = ModDestination.ModulationEnvelopeToPitchCents; return true;
            case SFGenerator.VibLfoToPitch: dest = ModDestination.VibratoLfoPitchDepthCents; return true;
            case SFGenerator.ModLfoToPitch: dest = ModDestination.ModulationLfoPitchDepthCents; return true;
            case SFGenerator.ModLfoToVolume: dest = ModDestination.ModulationLfoVolumeDepthDb; return true;
            case SFGenerator.Pan: dest = ModDestination.PanNormalized; return true;
            case SFGenerator.CoarseTune:
            case SFGenerator.FineTune: dest = ModDestination.PitchCents; return true;
            case SFGenerator.ReverbEffectsSend: dest = ModDestination.ReverbSendAmount; return true;
            case SFGenerator.ChorusEffectsSend: dest = ModDestination.ChorusSendAmount; return true;
            default: dest = default; return false;
        }
    }

    private static double ConvertAmount(ModDestination dest, SFGenerator gen, short amount)
    {
        switch (dest)
        {
            // SF2 centibels → IR decibels.
            case ModDestination.AttenuationDb:
            case ModDestination.FilterResonanceDb:
            case ModDestination.ModulationLfoVolumeDepthDb:
                return amount / 10.0;

            // Pitch is cents, but a CoarseTune destination is semitones.
            case ModDestination.PitchCents:
                return gen == SFGenerator.CoarseTune ? amount * 100.0 : amount;

            // Pan: SF2 tenths-of-percent, ±500 = hard L/R → normalized ±1.
            case ModDestination.PanNormalized:
                return amount / 500.0;

            // Effect sends: SF2 tenths-of-percent → 0..1 fraction.
            case ModDestination.ReverbSendAmount:
            case ModDestination.ChorusSendAmount:
                return amount / 1000.0;

            // Everything else (filter cutoff / mod-env / LFO depths) is plain cents.
            default:
                return amount;
        }
    }

    /// <summary>
    /// Map the source operator's curve bits (type / direction / polarity, SF2
    /// §8.2) to an IR transform. The IR curve set is a superset of what most
    /// banks use; unrepresentable combinations fall back to the closest match.
    /// </summary>
    private static ModTransform MapTransform(ushort srcOp)
    {
        var type = (srcOp >> 10) & 0x3F;
        var reverse = ((srcOp >> 8) & 0x1) == 1;   // direction D
        var bipolar = ((srcOp >> 9) & 0x1) == 1;   // polarity P

        return type switch
        {
            1 => reverse ? ModTransform.ConcaveUnipolarNegative : ModTransform.ConcaveUnipolar, // Concave
            2 => ModTransform.ConvexUnipolar,                                                    // Convex
            3 => ModTransform.Switch,                                                            // Switch
            _ => bipolar ? ModTransform.LinearBipolar : ModTransform.Linear,                     // Linear
        };
    }
}
