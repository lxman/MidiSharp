using System;
using System.Collections.Generic;
using SF2.Net;

namespace MidiSharp.SoundBank.Sf2;

/// <summary>
/// Top-level SF2 → IR translator. Walks the SoundFont's preset/instrument/zone
/// hierarchy once, applying SF2's instrument-SET / preset-ADD generator
/// semantics, and emits a flat <see cref="SoundBank"/> the synth can consume
/// without knowing SF2 ever existed.
/// </summary>
internal static class Sf2BankLoader
{
    public static SoundBank Load(SoundFont sf, SoundBankLoadOptions options)
    {
        var samples = BuildSampleSource(sf);
        var patches = BuildPatches(sf);

        return new SoundBank
        {
            Name = sf.Info.BankName,
            Author = sf.Info.Engineer,
            Copyright = sf.Info.Copyright,
            Comment = sf.Info.Comments,
            SourceFormat = SoundBankFormat.Sf2,
            Patches = patches,
            Samples = samples,
        };
    }

    // ── Patch / zone flattening ───────────────────────────────────────

    /// <summary>
    /// Walk the SF2 preset/instrument/zone hierarchy and emit flat IR patches.
    /// Shared with the SF3 loader — SF3 is structurally identical to SF2;
    /// only the sample-data encoding differs.
    /// </summary>
    internal static IReadOnlyList<Patch> BuildPatches(SoundFont sf)
    {
        var instrumentsByIndex = new Dictionary<int, Instrument>(sf.Instruments.Count);
        for (int i = 0; i < sf.Instruments.Count; i++)
            instrumentsByIndex[i] = sf.Instruments[i];

        var sampleIdByHeader = new Dictionary<SampleHeader, int>(sf.SampleHeaders.Count);
        for (int i = 0; i < sf.SampleHeaders.Count; i++)
            sampleIdByHeader[sf.SampleHeaders[i]] = i;

        var patches = new List<Patch>(sf.Presets.Count);
        foreach (var preset in sf.Presets)
        {
            var patch = BuildPatch(preset, instrumentsByIndex, sampleIdByHeader);
            // A zero-zone patch can never sound, and a stray empty preset sharing a
            // (bank, program) with a real one would shadow it in FindPatch's
            // last-wins lookup. Drop them.
            if (patch.Zones.Count > 0)
                patches.Add(patch);
        }
        return patches;
    }

    private static Patch BuildPatch(
        Preset preset,
        Dictionary<int, Instrument> instrumentsByIndex,
        Dictionary<SampleHeader, int> sampleIdByHeader)
    {
        var presetGlobal = preset.GlobalZone;
        var zones = new List<PatchZone>();

        foreach (var presetZone in preset.Zones)
        {
            if (ReferenceEquals(presetZone, presetGlobal)) continue;
            if (presetZone.InstrumentIndex < 0) continue;
            if (!instrumentsByIndex.TryGetValue(presetZone.InstrumentIndex, out var instrument)) continue;

            ExpandInstrumentZones(presetGlobal, presetZone, instrument, sampleIdByHeader, zones);
        }

        return new Patch
        {
            Bank = preset.Bank,
            Program = preset.Number,
            Name = preset.Name,
            Zones = zones,
        };
    }

    private static void ExpandInstrumentZones(
        Zone? presetGlobal,
        Zone presetZone,
        Instrument instrument,
        Dictionary<SampleHeader, int> sampleIdByHeader,
        List<PatchZone> output)
    {
        var instrumentGlobal = instrument.GlobalZone;

        foreach (var instZone in instrument.Zones)
        {
            if (ReferenceEquals(instZone, instrumentGlobal)) continue;
            if (instZone.Sample is null) continue;
            if (!sampleIdByHeader.TryGetValue(instZone.Sample, out var sampleId)) continue;

            var state = new Sf2GeneratorState();

            // Instrument-zone cascade SETs the baseline.
            if (instrumentGlobal != null) state.ApplySet(instrumentGlobal.Generators);
            state.ApplySet(instZone.Generators);

            // Preset-zone cascade ADDs deltas on top.
            if (presetGlobal != null) state.ApplyAdd(presetGlobal.Generators);
            state.ApplyAdd(presetZone.Generators);

            // KeyRange/VelRange intersections may have collapsed; skip empty zones.
            if (state.KeyRangeLow > state.KeyRangeHigh) continue;
            if (state.VelRangeLow > state.VelRangeHigh) continue;

            // Combine this zone's explicit modulators with the 10 defaults
            // (instrument modulators overwrite, preset modulators sum — SF2 §9.5).
            var routes = Sf2ModulatorTranslator.Combine(
                instrumentGlobal?.Modulators,
                instZone.Modulators,
                presetGlobal?.Modulators,
                presetZone.Modulators);

            output.Add(Sf2ZoneTranslator.Build(state, sampleId, routes));
        }
    }

    // ── Sample source construction ────────────────────────────────────

    private static MemoryMappedSf2SampleSource BuildSampleSource(SoundFont sf)
    {
        // Hold a zero-copy view over the file's smpl bytes rather than copying the whole sample
        // pool into a fresh short[] — a large font no longer costs ~2× its sample size on the heap.
        // The ReadOnlyMemory roots the underlying byte[], so it outlives this SoundFont safely.
        var smplBytes = sf.RawSampleBytes;

        var metadata = new SampleMetadata[sf.SampleHeaders.Count];
        var entries = new (long AbsoluteStart, long LengthFrames)[sf.SampleHeaders.Count];

        for (int i = 0; i < sf.SampleHeaders.Count; i++)
        {
            var hdr = sf.SampleHeaders[i];
            long absStart = hdr.Start;
            long length = (long)hdr.End - hdr.Start;
            long loopStart = (long)hdr.StartLoop - hdr.Start;
            long loopEnd = (long)hdr.EndLoop - hdr.Start;

            entries[i] = (absStart, length);

            metadata[i] = new SampleMetadata
            {
                Name = hdr.Name,
                SampleRate = (int)hdr.SampleRate,
                Channels = 1,
                LengthFrames = length,
                LoopStartFrames = Math.Max(0, loopStart),
                LoopEndFrames = Math.Max(0, loopEnd),
                RootKey = hdr.OriginalPitch,
                PitchCorrectionCents = hdr.PitchCorrection,
                StereoLinkSampleId = ResolveStereoLink(hdr, sf.SampleHeaders),
            };
        }

        return new MemoryMappedSf2SampleSource(smplBytes, metadata, entries);
    }

    private static int? ResolveStereoLink(SampleHeader hdr, IReadOnlyList<SampleHeader> all)
    {
        if (hdr.SampleType is SFSampleLink.MonoSample or SFSampleLink.RomMonoSample) return null;
        if (hdr.SampleLink == 0) return null;

        // SampleLink is the partner header's index in the original SHDR list.
        // SF2.Net removes the terminal EOS entry, so the indices should still align.
        int link = hdr.SampleLink;
        if (link < 0 || link >= all.Count) return null;
        return link;
    }
}
