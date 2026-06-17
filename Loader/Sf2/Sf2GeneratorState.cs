using System.Collections.Generic;
using Loader.Sf2.Enums;
using Loader.Sf2.Model;

namespace Loader.Sf2;

/// <summary>
/// Mutable accumulator for SF2 generator values during preset-instrument
/// hierarchy flattening. Holds every generator the IR cares about with its
/// SF2-spec default; instrument-zone generators replace defaults (SET),
/// preset-zone generators add to the accumulated total (ADD).
/// </summary>
/// <remarks>
/// Values are kept in their raw SF2 signed-short encoding until
/// <see cref="ToPatchZone"/> runs the conversions. Keeping the encoding native
/// during accumulation means SETs and ADDs compose without precision loss from
/// intermediate floating-point math.
/// </remarks>
internal sealed class Sf2GeneratorState
{
    // ─── Sample addressing offsets (instrument-zone only) ─────────────
    public int StartAddrsOffset;
    public int EndAddrsOffset;
    public int StartloopAddrsOffset;
    public int EndloopAddrsOffset;

    // ─── Pitch ───────────────────────────────────────────────────────
    public short CoarseTune;          // semitones, default 0
    public short FineTune;            // cents, default 0
    public short ScaleTuning = 100;   // cents per key, default 100
    public short OverridingRootKey = -1;

    // ─── Filter ───────────────────────────────────────────────────────
    public short InitialFilterFc = 13500;  // ≈ 19914 Hz (essentially open)
    public short InitialFilterQ;            // 0 cB

    // ─── Volume envelope (timecents; sustain in cB) ──────────────────
    public short DelayVolEnv = -12000;
    public short AttackVolEnv = -12000;
    public short HoldVolEnv = -12000;
    public short DecayVolEnv = -12000;
    public short SustainVolEnv;             // 0 cB = full sustain
    public short ReleaseVolEnv = -12000;
    public short KeynumToVolEnvHold;
    public short KeynumToVolEnvDecay;

    // ─── Modulation envelope ─────────────────────────────────────────
    public short DelayModEnv = -12000;
    public short AttackModEnv = -12000;
    public short HoldModEnv = -12000;
    public short DecayModEnv = -12000;
    public short SustainModEnv;
    public short ReleaseModEnv = -12000;
    public short KeynumToModEnvHold;
    public short KeynumToModEnvDecay;
    public short ModEnvToPitch;             // cents
    public short ModEnvToFilterFc;          // cents

    // ─── Vibrato LFO ──────────────────────────────────────────────────
    public short DelayVibLFO = -12000;
    public short FreqVibLFO;                // 0 abs cents = 8.176 Hz
    public short VibLfoToPitch;             // cents

    // ─── Modulation LFO ───────────────────────────────────────────────
    public short DelayModLFO = -12000;
    public short FreqModLFO;
    public short ModLfoToPitch;             // cents
    public short ModLfoToFilterFc;          // cents
    public short ModLfoToVolume;            // cB

    // ─── Level & sends ────────────────────────────────────────────────
    public short InitialAttenuation;        // cB; EMU 0.4 factor applied at convert time
    public short Pan;                       // 0.1% units; ±500 = ±50%
    public short ReverbEffectsSend;         // 0.1% units
    public short ChorusEffectsSend;         // 0.1% units

    // ─── Activation / addressing ─────────────────────────────────────
    public byte KeyRangeLow;
    public byte KeyRangeHigh = 127;
    public byte VelRangeLow;
    public byte VelRangeHigh = 127;
    public short ExclusiveClass;            // 0 = none
    public short SampleModes;               // 0 = no loop

    /// <summary>Apply gens as SET (instrument-zone semantics: replace default).</summary>
    public void ApplySet(IReadOnlyList<Generator> generators)
    {
        foreach (var gen in generators) ApplyOne(gen, isPresetLevel: false);
    }

    /// <summary>Apply gens as ADD (preset-zone semantics: add to accumulated value).</summary>
    public void ApplyAdd(IReadOnlyList<Generator> generators)
    {
        foreach (var gen in generators) ApplyOne(gen, isPresetLevel: true);
    }

    private void ApplyOne(Generator gen, bool isPresetLevel)
    {
        var a = gen.Amount;
        switch (gen.Operator)
        {
            // KeyRange / VelRange use intersection semantics, not SET or ADD.
            case SFGenerator.KeyRange:
                KeyRangeLow = System.Math.Max(KeyRangeLow, a.Range.Low);
                KeyRangeHigh = System.Math.Min(KeyRangeHigh, a.Range.High);
                break;
            case SFGenerator.VelRange:
                VelRangeLow = System.Math.Max(VelRangeLow, a.Range.Low);
                VelRangeHigh = System.Math.Min(VelRangeHigh, a.Range.High);
                break;

            case SFGenerator.StartAddrsOffset:           AddOrSet(ref StartAddrsOffset, a.Signed, isPresetLevel); break;
            case SFGenerator.EndAddrsOffset:             AddOrSet(ref EndAddrsOffset, a.Signed, isPresetLevel); break;
            case SFGenerator.StartloopAddrsOffset:       AddOrSet(ref StartloopAddrsOffset, a.Signed, isPresetLevel); break;
            case SFGenerator.EndloopAddrsOffset:         AddOrSet(ref EndloopAddrsOffset, a.Signed, isPresetLevel); break;
            case SFGenerator.StartAddrsCoarseOffset:     AddOrSet(ref StartAddrsOffset, a.Signed * 32768, isPresetLevel); break;
            case SFGenerator.EndAddrsCoarseOffset:       AddOrSet(ref EndAddrsOffset, a.Signed * 32768, isPresetLevel); break;
            case SFGenerator.StartloopAddrsCoarseOffset: AddOrSet(ref StartloopAddrsOffset, a.Signed * 32768, isPresetLevel); break;
            case SFGenerator.EndloopAddrsCoarseOffset:   AddOrSet(ref EndloopAddrsOffset, a.Signed * 32768, isPresetLevel); break;

            case SFGenerator.CoarseTune:                 AddOrSet(ref CoarseTune, a.Signed, isPresetLevel); break;
            case SFGenerator.FineTune:                   AddOrSet(ref FineTune, a.Signed, isPresetLevel); break;
            case SFGenerator.ScaleTuning:                AddOrSet(ref ScaleTuning, a.Signed, isPresetLevel); break;
            case SFGenerator.OverridingRootKey:          OverridingRootKey = a.Signed; break;

            case SFGenerator.InitialFilterFc:            AddOrSet(ref InitialFilterFc, a.Signed, isPresetLevel); break;
            case SFGenerator.InitialFilterQ:             AddOrSet(ref InitialFilterQ, a.Signed, isPresetLevel); break;

            case SFGenerator.DelayVolEnv:                AddOrSet(ref DelayVolEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.AttackVolEnv:               AddOrSet(ref AttackVolEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.HoldVolEnv:                 AddOrSet(ref HoldVolEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.DecayVolEnv:                AddOrSet(ref DecayVolEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.SustainVolEnv:              AddOrSet(ref SustainVolEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.ReleaseVolEnv:              AddOrSet(ref ReleaseVolEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.KeynumToVolEnvHold:         AddOrSet(ref KeynumToVolEnvHold, a.Signed, isPresetLevel); break;
            case SFGenerator.KeynumToVolEnvDecay:        AddOrSet(ref KeynumToVolEnvDecay, a.Signed, isPresetLevel); break;

            case SFGenerator.DelayModEnv:                AddOrSet(ref DelayModEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.AttackModEnv:               AddOrSet(ref AttackModEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.HoldModEnv:                 AddOrSet(ref HoldModEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.DecayModEnv:                AddOrSet(ref DecayModEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.SustainModEnv:              AddOrSet(ref SustainModEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.ReleaseModEnv:              AddOrSet(ref ReleaseModEnv, a.Signed, isPresetLevel); break;
            case SFGenerator.KeynumToModEnvHold:         AddOrSet(ref KeynumToModEnvHold, a.Signed, isPresetLevel); break;
            case SFGenerator.KeynumToModEnvDecay:        AddOrSet(ref KeynumToModEnvDecay, a.Signed, isPresetLevel); break;
            case SFGenerator.ModEnvToPitch:              AddOrSet(ref ModEnvToPitch, a.Signed, isPresetLevel); break;
            case SFGenerator.ModEnvToFilterFc:           AddOrSet(ref ModEnvToFilterFc, a.Signed, isPresetLevel); break;

            case SFGenerator.DelayVibLFO:                AddOrSet(ref DelayVibLFO, a.Signed, isPresetLevel); break;
            case SFGenerator.FreqVibLFO:                 AddOrSet(ref FreqVibLFO, a.Signed, isPresetLevel); break;
            case SFGenerator.VibLfoToPitch:              AddOrSet(ref VibLfoToPitch, a.Signed, isPresetLevel); break;

            case SFGenerator.DelayModLFO:                AddOrSet(ref DelayModLFO, a.Signed, isPresetLevel); break;
            case SFGenerator.FreqModLFO:                 AddOrSet(ref FreqModLFO, a.Signed, isPresetLevel); break;
            case SFGenerator.ModLfoToPitch:              AddOrSet(ref ModLfoToPitch, a.Signed, isPresetLevel); break;
            case SFGenerator.ModLfoToFilterFc:           AddOrSet(ref ModLfoToFilterFc, a.Signed, isPresetLevel); break;
            case SFGenerator.ModLfoToVolume:             AddOrSet(ref ModLfoToVolume, a.Signed, isPresetLevel); break;

            case SFGenerator.InitialAttenuation:         AddOrSet(ref InitialAttenuation, a.Signed, isPresetLevel); break;
            case SFGenerator.Pan:                        AddOrSet(ref Pan, a.Signed, isPresetLevel); break;
            case SFGenerator.ReverbEffectsSend:          AddOrSet(ref ReverbEffectsSend, a.Signed, isPresetLevel); break;
            case SFGenerator.ChorusEffectsSend:          AddOrSet(ref ChorusEffectsSend, a.Signed, isPresetLevel); break;

            case SFGenerator.ExclusiveClass:             ExclusiveClass = a.Signed; break;
            case SFGenerator.SampleModes:                SampleModes = a.Signed; break;

            // Terminal/index generators handled by the caller (SampleID, Instrument)
            // and key/velocity-fixing generators (Keynum, Velocity) are ignored here.
        }
    }

    private static void AddOrSet(ref int field, int delta, bool add)
    {
        if (add) field += delta; else field = delta;
    }

    private static void AddOrSet(ref short field, int delta, bool add)
    {
        int v = add ? field + delta : delta;
        if (v > short.MaxValue) v = short.MaxValue;
        else if (v < short.MinValue) v = short.MinValue;
        field = (short)v;
    }
}
