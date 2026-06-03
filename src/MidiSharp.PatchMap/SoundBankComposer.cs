using System.Collections.Generic;
using System.Linq;
using MidiSharp.SoundBank;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap;

/// <summary>
/// Builds a composite <see cref="IRBank"/> from a base font plus per-patch overrides drawn
/// from other fonts. The synth consumes the result through the ordinary
/// <c>FindPatch</c>/<c>Samples</c> contract and cannot tell it from a natively-loaded bank.
/// </summary>
/// <remarks>
/// The sample-id space is the concatenation [base | source0 | source1 | ...]. Base patches keep
/// their ids unchanged (offset 0); an overridden patch's zones are cloned with their sample ids
/// shifted by the source font's offset. The composite borrows its inputs' sample sources (it
/// does not dispose them) — the owning <see cref="PatchMapSession"/> does.
/// </remarks>
public static class SoundBankComposer
{
    /// <summary>
    /// Compose <paramref name="baseBank"/> with <paramref name="overrides"/> (logical
    /// (bank, program) → a patch in some source font). Overrides whose source patch can't be
    /// found are skipped, leaving the base font's patch (and its bank-0 fallback) in place.
    /// </summary>
    public static IRBank BuildComposite(
        IRBank baseBank,
        IReadOnlyDictionary<(int Bank, int Program), PatchRef> overrides)
    {
        // Assign each contributing font a sample-id offset. Base is always first at offset 0.
        var offsetOf = new Dictionary<IRBank, int> { [baseBank] = 0 };
        var extraSources = new List<IRBank>();
        var running = baseBank.Samples.Count;
        foreach (var pref in overrides.Values)
        {
            if (offsetOf.ContainsKey(pref.Source)) continue;
            offsetOf[pref.Source] = running;
            extraSources.Add(pref.Source);
            running += pref.Source.Samples.Count;
        }

        // Start from the base patches (ids valid as-is at offset 0), then apply overrides.
        var byKey = new Dictionary<(int, int), Patch>();
        foreach (var p in baseBank.Patches)
            byKey[(p.Bank, p.Program)] = p;

        foreach (var entry in overrides)
        {
            var (logicalBank, logicalProgram) = entry.Key;
            var pref = entry.Value;
            var srcPatch = pref.Source.FindPatch(pref.Bank, pref.Program);
            if (srcPatch == null) continue;   // unresolved → leave base/fallback in place

            var offset = offsetOf[pref.Source];
            var zones = offset == 0 ? srcPatch.Zones : CloneZonesWithOffset(srcPatch.Zones, offset);
            byKey[(logicalBank, logicalProgram)] = new Patch
            {
                Bank = logicalBank,
                Program = logicalProgram,
                Name = srcPatch.Name,
                Zones = zones,
            };
        }

        var sampleSources = new List<ISampleSource>(1 + extraSources.Count) { baseBank.Samples };
        sampleSources.AddRange(extraSources.Select(s => s.Samples));

        return new IRBank
        {
            Name = baseBank.Name,
            Author = baseBank.Author,
            Copyright = baseBank.Copyright,
            Comment = baseBank.Comment,
            SourceFormat = baseBank.SourceFormat,
            Patches = byKey.Values.ToList(),
            Samples = new ConcatenatedSampleSource(sampleSources),
        };
    }

    // PatchZone/SampleRef are immutable; re-index a borrowed zone's sample by cloning it with
    // SampleId shifted into the composite's concatenated sample space.
    private static IReadOnlyList<PatchZone> CloneZonesWithOffset(IReadOnlyList<PatchZone> zones, int offset)
    {
        var result = new PatchZone[zones.Count];
        for (var i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            var s = z.Sample;
            result[i] = new PatchZone
            {
                Keys = z.Keys,
                Velocities = z.Velocities,
                CCGates = z.CCGates,
                KeySwitch = z.KeySwitch,
                RoundRobin = z.RoundRobin,
                ExclusiveGroup = z.ExclusiveGroup,
                Sample = new SampleRef
                {
                    SampleId = s.SampleId + offset,
                    LoopMode = s.LoopMode,
                    OverridingRootKey = s.OverridingRootKey,
                    FineTuneCents = s.FineTuneCents,
                    CoarseTuneSemitones = s.CoarseTuneSemitones,
                    ScaleTuningCentsPerKey = s.ScaleTuningCentsPerKey,
                    StartOffset = s.StartOffset,
                    EndOffset = s.EndOffset,
                    LoopStartOffset = s.LoopStartOffset,
                    LoopEndOffset = s.LoopEndOffset,
                },
                Pitch = z.Pitch,
                Level = z.Level,
                VolumeEnvelope = z.VolumeEnvelope,
                ModulationEnvelope = z.ModulationEnvelope,
                VibratoLFO = z.VibratoLFO,
                ModulationLFO = z.ModulationLFO,
                Filter = z.Filter,
                ReverbSend = z.ReverbSend,
                ChorusSend = z.ChorusSend,
                Routes = z.Routes,
            };
        }
        return result;
    }
}
