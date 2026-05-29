using System.Collections.Generic;
using SF2.Net;
using static MidiSharp.SoundBank.Sf2.Sf2UnitConversions;

namespace MidiSharp.SoundBank.Sf2;

/// <summary>
/// Translates a flattened SF2 generator state plus a sample reference into one
/// IR <see cref="PatchZone"/>. The hierarchy walking (preset-global,
/// preset-zone, instrument-global, instrument-zone) is the caller's job; this
/// type's contract is "given the accumulated state, produce the IR record."
/// </summary>
internal static class Sf2ZoneTranslator
{
    /// <summary>
    /// Build a PatchZone from the accumulated generator state and the resolved
    /// sample. Optional sub-records (filter, LFOs, modulation envelope) are
    /// emitted only when their corresponding generators are non-default, so
    /// the synth's "is this feature active?" checks stay branch-cheap.
    /// </summary>
    public static PatchZone Build(Sf2GeneratorState state, int sampleId, IReadOnlyList<ModulationRoute> routes)
    {
        return new PatchZone
        {
            Keys = new KeyRange(state.KeyRangeLow, state.KeyRangeHigh),
            Velocities = new VelocityRange(state.VelRangeLow, state.VelRangeHigh),
            ExclusiveGroup = state.ExclusiveClass > 0 ? state.ExclusiveClass : null,

            Sample = BuildSampleRef(state, sampleId),
            Pitch = BuildPitch(state),
            Level = BuildLevel(state),

            VolumeEnvelope = BuildVolumeEnvelope(state),
            ModulationEnvelope = BuildModulationEnvelopeIfActive(state),
            VibratoLFO = BuildVibratoLfoIfActive(state),
            ModulationLFO = BuildModulationLfoIfActive(state),
            Filter = BuildFilterIfActive(state),

            ReverbSend = TenthOfPercentToFraction(state.ReverbEffectsSend),
            ChorusSend = TenthOfPercentToFraction(state.ChorusEffectsSend),

            Routes = routes,
        };
    }

    private static SampleRef BuildSampleRef(Sf2GeneratorState state, int sampleId)
    {
        var loopMode = (state.SampleModes & 0x3) switch
        {
            1 => LoopMode.Continuous,
            3 => LoopMode.UntilRelease,
            _ => LoopMode.None,
        };

        return new SampleRef
        {
            SampleId = sampleId,
            LoopMode = loopMode,
            OverridingRootKey = state.OverridingRootKey >= 0 ? state.OverridingRootKey : (int?)null,
            FineTuneCents = 0,                          // folded into PitchSettings; sample-level is on metadata
            CoarseTuneSemitones = 0,
            ScaleTuningCentsPerKey = state.ScaleTuning,
            StartOffset = state.StartAddrsOffset != 0 ? state.StartAddrsOffset : (long?)null,
            EndOffset = state.EndAddrsOffset != 0 ? state.EndAddrsOffset : (long?)null,
            LoopStartOffset = state.StartloopAddrsOffset != 0 ? state.StartloopAddrsOffset : (long?)null,
            LoopEndOffset = state.EndloopAddrsOffset != 0 ? state.EndloopAddrsOffset : (long?)null,
        };
    }

    private static PitchSettings BuildPitch(Sf2GeneratorState state) => new()
    {
        FineTuneCents = state.FineTune,
        CoarseTuneSemitones = state.CoarseTune,
        ModulationEnvelopeDepthCents = state.ModEnvToPitch,
    };

    private static LevelSettings BuildLevel(Sf2GeneratorState state) => new()
    {
        AttenuationDb = CentibelsToDb(state.InitialAttenuation) * EmuAttenuationFactor,
        Pan = TenthOfPercentToFraction(state.Pan) * 2.0,   // SF2 ±500 = ±50% → IR ±1.0
    };

    private static EnvelopeSettings BuildVolumeEnvelope(Sf2GeneratorState state) => new()
    {
        DelaySeconds = TimecentsToSeconds(state.DelayVolEnv),
        AttackSeconds = TimecentsToSeconds(state.AttackVolEnv),
        HoldSeconds = TimecentsToSeconds(state.HoldVolEnv),
        DecaySeconds = TimecentsToSeconds(state.DecayVolEnv),
        SustainLevel = SustainCentibelsToLinear(state.SustainVolEnv),
        ReleaseSeconds = TimecentsToSeconds(state.ReleaseVolEnv),
        KeynumToHoldCentsPerKey = state.KeynumToVolEnvHold,
        KeynumToDecayCentsPerKey = state.KeynumToVolEnvDecay,
    };

    private static EnvelopeSettings? BuildModulationEnvelopeIfActive(Sf2GeneratorState state)
    {
        // SF2 always carries a mod envelope, but it's only "active" if it has
        // a non-zero modulation destination (filter or pitch). When both are
        // zero, the envelope contributes nothing and is omitted.
        if (state.ModEnvToFilterFc == 0 && state.ModEnvToPitch == 0) return null;

        return new EnvelopeSettings
        {
            DelaySeconds = TimecentsToSeconds(state.DelayModEnv),
            AttackSeconds = TimecentsToSeconds(state.AttackModEnv),
            HoldSeconds = TimecentsToSeconds(state.HoldModEnv),
            DecaySeconds = TimecentsToSeconds(state.DecayModEnv),
            SustainLevel = SustainCentibelsToLinear(state.SustainModEnv),
            ReleaseSeconds = TimecentsToSeconds(state.ReleaseModEnv),
            KeynumToHoldCentsPerKey = state.KeynumToModEnvHold,
            KeynumToDecayCentsPerKey = state.KeynumToModEnvDecay,
        };
    }

    private static LFOSettings? BuildVibratoLfoIfActive(Sf2GeneratorState state)
    {
        if (state.VibLfoToPitch == 0) return null;
        return new LFOSettings
        {
            DelaySeconds = TimecentsToSeconds(state.DelayVibLFO),
            FrequencyHz = AbsoluteCentsToHz(state.FreqVibLFO),
            PitchDepthCents = state.VibLfoToPitch,
        };
    }

    private static LFOSettings? BuildModulationLfoIfActive(Sf2GeneratorState state)
    {
        if (state.ModLfoToPitch == 0 && state.ModLfoToFilterFc == 0 && state.ModLfoToVolume == 0) return null;
        return new LFOSettings
        {
            DelaySeconds = TimecentsToSeconds(state.DelayModLFO),
            FrequencyHz = AbsoluteCentsToHz(state.FreqModLFO),
            PitchDepthCents = state.ModLfoToPitch,
            FilterDepthCents = state.ModLfoToFilterFc,
            VolumeDepthDb = CentibelsToDb(state.ModLfoToVolume),
        };
    }

    private static FilterSettings? BuildFilterIfActive(Sf2GeneratorState state)
    {
        // SF2 default 13500 abs cents ≈ 19914 Hz — past the Nyquist of any
        // common rate. Treat that as "filter inactive" so the synth skips it.
        if (state.InitialFilterFc >= 13500 && state.InitialFilterQ == 0
            && state.ModEnvToFilterFc == 0 && state.ModLfoToFilterFc == 0)
        {
            return null;
        }

        return new FilterSettings
        {
            Type = FilterType.LowPass,
            CutoffHz = AbsoluteCentsToHz(state.InitialFilterFc),
            ResonanceDb = CentibelsToDb(state.InitialFilterQ),
            EnvelopeDepthCents = state.ModEnvToFilterFc,
            LfoDepthCents = state.ModLfoToFilterFc,
            VelocityToCutoffCents = -2400.0,    // SF2 default modulator #2 fast path
        };
    }
}
