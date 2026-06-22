using System.Collections.Generic;
using MidiSharp.SoundBank;
using IRBank = MidiSharp.SoundBank.SoundBank;
namespace MidiSharp.Loader.Dls;

/// <summary>
/// DLS → IR translator. Walks <see cref="DlsCollection"/>.Instruments × Regions,
/// resolves each region's wave reference, merges instrument-level + region-level
/// articulators into <see cref="ModulationRoute"/>s, and emits flat
/// <see cref="PatchZone"/> records.
/// </summary>
/// <remarks>
/// DLS Level 1 articulators (<c>art1</c>) and Level 2 (<c>art2</c>) use the same
/// connection-block format; the level distinction matters for which sources
/// and destinations are guaranteed available, not for parsing. We support both
/// uniformly and drop connections we can't express in the IR (LFO/EG internal
/// destinations are wired through the zone's static settings instead).
/// </remarks>
internal static class DlsBankLoader
{
    public static IRBank Load(DlsCollection col, SoundBankLoadOptions options)
    {
        var samples = new DlsWaveTableSampleSource(col.Waves);
        IReadOnlyList<Patch> patches = BuildPatches(col);

        return new IRBank
        {
            Name = col.Name ?? string.Empty,
            Author = col.Engineer,
            Copyright = col.Copyright,
            Comment = col.Comments,
            SourceFormat = SoundBankFormat.Dls,
            Patches = patches,
            Samples = samples,
        };
    }

    private static IReadOnlyList<Patch> BuildPatches(DlsCollection col)
    {
        var patches = new List<Patch>(col.Instruments.Count);
        foreach (DlsInstrument? inst in col.Instruments)
        {
            var zones = new List<PatchZone>(inst.Regions.Count);
            foreach (DlsRegion? region in inst.Regions)
            {
                if ((int)region.WaveLink.TableIndex >= col.Waves.Count) continue;
                zones.Add(BuildZone(inst, region, col.Waves[(int)region.WaveLink.TableIndex]));
            }
            patches.Add(new Patch
            {
                Bank = (int)inst.Bank,
                Program = (int)inst.Program,
                Name = inst.Name ?? string.Empty,
                Zones = zones,
            });
        }
        return patches;
    }

    private static PatchZone BuildZone(DlsInstrument inst, DlsRegion region, DlsWave wave)
    {
        // Build the zone by walking ALL articulator connections — instrument-level
        // first, then region-level (so region overrides instrument). The accumulator
        // fields each connection by source type: None → static, internal modulator
        // → depth, external MIDI → runtime route.
        var b = new DlsZoneBuilder();
        foreach (ArticulatorList? artList in inst.Articulators)
            b.Apply(artList.Connections);
        foreach (ArticulatorList? artList in region.Articulators)
            b.Apply(artList.Connections);

        // Prepend DLS Level 2 default-articulation routes so banks that omit
        // (e.g.) velocity → gain still get the spec-required behavior. Bank-
        // supplied routes follow and stack additively on the same destinations.
        var allRoutes = new List<ModulationRoute>(DlsDefaultArticulation.DefaultRoutes.Length + b.Routes.Count);
        allRoutes.AddRange(DlsDefaultArticulation.DefaultRoutes);
        allRoutes.AddRange(b.Routes);

        // Wsmp metadata sits outside the articulator system: tuning, gain, loops.
        // Region wsmp overrides wave wsmp.
        WaveSampleInfo? wsmp = region.SampleInfo ?? wave.SampleInfo;
        int rootKey = wsmp?.UnityNote ?? 60;
        double fineTune = wsmp?.FineTuneCents ?? 0;
        // DLS wsmp gain stores attenuation as cB (already negated by the reader so
        // positive = quieter). Fold into the accumulator's running attenuation.
        if (wsmp != null && wsmp.GainCentibels > 0)
            b.AttenuationDb += wsmp.GainCentibels / 10.0;

        var loopMode = LoopMode.None;
        if (wsmp != null && wsmp.Loops.Count > 0)
        {
            loopMode = wsmp.Loops[0].LoopType switch
            {
                DlsLoopType.Forward => LoopMode.Continuous,
                DlsLoopType.Release => LoopMode.UntilRelease,
                _ => LoopMode.None,
            };
        }

        return new PatchZone
        {
            Keys = new KeyRange(region.KeyLow, region.KeyHigh),
            Velocities = new VelocityRange(region.VelocityLow, region.VelocityHigh),
            ExclusiveGroup = region.KeyGroup > 0 ? region.KeyGroup : null,

            Sample = new SampleRef
            {
                SampleId = (int)region.WaveLink.TableIndex,
                LoopMode = loopMode,
                OverridingRootKey = rootKey,
                FineTuneCents = fineTune,
                ScaleTuningCentsPerKey = 100.0,
            },

            Pitch = new PitchSettings
            {
                ModulationEnvelopeDepthCents = b.PitchModEnvCents,
            },
            Level = new LevelSettings
            {
                AttenuationDb = b.AttenuationDb,
                Pan = b.PanNormalized,
            },

            VolumeEnvelope = new EnvelopeSettings
            {
                DelaySeconds = b.VolDelaySec,
                AttackSeconds = b.VolAttackSec,
                HoldSeconds = b.VolHoldSec,
                DecaySeconds = b.VolDecaySec,
                SustainLevel = b.VolSustainLevel,
                ReleaseSeconds = b.VolReleaseSec,
            },
            ModulationEnvelope = b.HasModEnvelope
                ? new EnvelopeSettings
                {
                    DelaySeconds = b.ModDelaySec,
                    AttackSeconds = b.ModAttackSec,
                    HoldSeconds = b.ModHoldSec,
                    DecaySeconds = b.ModDecaySec,
                    SustainLevel = b.ModSustainLevel,
                    ReleaseSeconds = b.ModReleaseSec,
                }
                : null,

            VibratoLFO = b.HasVibLfo
                ? new LFOSettings
                {
                    DelaySeconds = b.VibLfoDelaySec,
                    FrequencyHz = b.VibLfoFreqHz,
                    PitchDepthCents = b.VibLfoPitchCents,
                }
                : null,
            ModulationLFO = b.HasModLfo
                ? new LFOSettings
                {
                    DelaySeconds = b.ModLfoDelaySec,
                    FrequencyHz = b.ModLfoFreqHz,
                    PitchDepthCents = b.ModLfoPitchCents,
                    VolumeDepthDb = b.ModLfoVolumeDb,
                    FilterDepthCents = b.ModLfoFilterCents,
                }
                : null,

            Filter = b.HasFilter
                ? new FilterSettings
                {
                    Type = FilterType.LowPass,
                    CutoffHz = b.FilterCutoffHz,
                    ResonanceDb = b.FilterResonanceDb,
                    EnvelopeDepthCents = b.FilterModEnvCents,
                    LfoDepthCents = b.ModLfoFilterCents,
                }
                : null,

            ReverbSend = b.ReverbSend,
            ChorusSend = b.ChorusSend,

            Routes = allRoutes,
        };
    }
}
