using System.Collections.Generic;
using System.Linq;
using MidiSharp.SoundBank;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap;

/// <summary>
/// The output of composing a bank: the composite <see cref="IRBank"/> the synth loads, plus a
/// map from a MIDI track index to the synthetic (bank, program) address its forced instrument
/// lives at. Hand the map to <c>Synthesizer.SetTrackPatchMap</c> so notes from that track route
/// to the override regardless of the channel/program they carry.
/// </summary>
public readonly struct CompositeResult
{
    public CompositeResult(IRBank bank, IReadOnlyDictionary<int, (int Bank, int Program)> trackPatchMap)
    {
        Bank = bank;
        TrackPatchMap = trackPatchMap;
    }

    /// <summary>The composite bank to load into the synth.</summary>
    public IRBank Bank { get; }

    /// <summary>trackIndex → synthetic (bank, program) for each resolved per-track override.</summary>
    public IReadOnlyDictionary<int, (int Bank, int Program)> TrackPatchMap { get; }
}

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
    /// Reserved logical bank for per-track override patches. A track override is placed at
    /// (<see cref="TrackOverrideBank"/>, trackIndex) so the synth can resolve it through the
    /// ordinary <c>FindPatch</c> contract. This value lies far above anything
    /// <c>BankResolution.Resolve</c> can produce (its max is the 14-bit 16383; drums are 128),
    /// so it never collides with a channel-resolved bank.
    /// </summary>
    public const int TrackOverrideBank = 1_000_000;

    /// <summary>
    /// Compose <paramref name="overrides"/> (logical (bank, program) → a patch in some source
    /// font). Overrides whose source patch can't be found are skipped, leaving the base font's
    /// patch (and its bank-0 fallback) in place.
    /// </summary>
    public static IRBank BuildComposite(
        IRBank baseBank,
        IReadOnlyDictionary<(int Bank, int Program), PatchRef> overrides)
        => BuildComposite(baseBank, overrides, EmptyTrackOverrides).Bank;

    private static readonly IReadOnlyDictionary<int, PatchRef> EmptyTrackOverrides
        = new Dictionary<int, PatchRef>();

    /// <summary>
    /// Compose <paramref name="baseBank"/> with per-patch <paramref name="patchOverrides"/> and
    /// per-track <paramref name="trackOverrides"/>. Track overrides are placed at the reserved
    /// (<see cref="TrackOverrideBank"/>, trackIndex) addresses and reported back in
    /// <see cref="CompositeResult.TrackPatchMap"/> so the synth can route a note from a given
    /// track to its forced instrument. Overrides whose source patch can't be found are skipped.
    /// </summary>
    public static CompositeResult BuildComposite(
        IRBank baseBank,
        IReadOnlyDictionary<(int Bank, int Program), PatchRef> patchOverrides,
        IReadOnlyDictionary<int, PatchRef> trackOverrides)
    {
        // Assign each contributing font a sample-id offset. Base is always first at offset 0.
        var offsetOf = new Dictionary<IRBank, int> { [baseBank] = 0 };
        var extraSources = new List<IRBank>();
        var running = baseBank.Samples.Count;
        void Reserve(PatchRef pref)
        {
            if (offsetOf.ContainsKey(pref.Source)) return;
            offsetOf[pref.Source] = running;
            extraSources.Add(pref.Source);
            running += pref.Source.Samples.Count;
        }
        foreach (var pref in patchOverrides.Values) Reserve(pref);
        foreach (var pref in trackOverrides.Values) Reserve(pref);

        // Start from the base patches (ids valid as-is at offset 0), then apply overrides.
        var byKey = new Dictionary<(int, int), Patch>();
        foreach (var p in baseBank.Patches)
            byKey[(p.Bank, p.Program)] = p;

        // A composed patch at a logical address, drawn from a source font with its sample ids
        // shifted into the concatenated sample space. Returns null when the source patch is absent.
        Patch? Compose(int logicalBank, int logicalProgram, PatchRef pref)
        {
            var srcPatch = pref.Source.FindPatch(pref.Bank, pref.Program);
            if (srcPatch == null) return null;   // unresolved → caller leaves base/fallback in place
            var offset = offsetOf[pref.Source];
            var zones = offset == 0 ? srcPatch.Zones : CloneZonesWithOffset(srcPatch.Zones, offset);
            return new Patch
            {
                Bank = logicalBank,
                Program = logicalProgram,
                Name = srcPatch.Name,
                Zones = zones,
            };
        }

        foreach (var entry in patchOverrides)
        {
            var (logicalBank, logicalProgram) = entry.Key;
            var composed = Compose(logicalBank, logicalProgram, entry.Value);
            if (composed != null) byKey[(logicalBank, logicalProgram)] = composed;
        }

        // Per-track overrides occupy the reserved bank; record trackIndex → synthetic address.
        var trackPatchMap = new Dictionary<int, (int Bank, int Program)>();
        foreach (var entry in trackOverrides)
        {
            var trackIndex = entry.Key;
            var composed = Compose(TrackOverrideBank, trackIndex, entry.Value);
            if (composed == null) continue;   // unresolved → track keeps its channel-based sound
            byKey[(TrackOverrideBank, trackIndex)] = composed;
            trackPatchMap[trackIndex] = (TrackOverrideBank, trackIndex);
        }

        var sampleSources = new List<ISampleSource>(1 + extraSources.Count) { baseBank.Samples };
        sampleSources.AddRange(extraSources.Select(s => s.Samples));

        var bank = new IRBank
        {
            Name = baseBank.Name,
            Author = baseBank.Author,
            Copyright = baseBank.Copyright,
            Comment = baseBank.Comment,
            SourceFormat = baseBank.SourceFormat,
            Patches = byKey.Values.ToList(),
            Samples = new ConcatenatedSampleSource(sampleSources),
        };
        return new CompositeResult(bank, trackPatchMap);
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
