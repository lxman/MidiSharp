using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MidiSharp.Loader.Sf2.Enums;
using MidiSharp.Loader.Sf2.Io;
using MidiSharp.Loader.Sf2.Model;

namespace MidiSharp.Loader.Sf2;

/// <summary>
/// A loaded SoundFont 2 file. Use <see cref="Load(string)"/> or <see cref="Load(byte[])"/>.
/// </summary>
public sealed class SoundFont
{
    private readonly SdtaChunkReader _sdta;
    private readonly SampleHeader[] _allSampleHeaders;

    /// <summary>Decoded INFO chunk metadata.</summary>
    public InfoMetadata Info { get; }

    /// <summary>All presets in the file, in their original order.</summary>
    public IReadOnlyList<Preset> Presets { get; }

    /// <summary>All instruments in the file, in their original order.</summary>
    public IReadOnlyList<Instrument> Instruments { get; }

    /// <summary>All sample headers in the file (the terminal "EOS" record has been removed).</summary>
    public IReadOnlyList<SampleHeader> SampleHeaders => _allSampleHeaders;

    /// <summary>Number of 16-bit sample frames in the smpl chunk.</summary>
    public int SmplFrameCount => _sdta.FrameCount;

    /// <summary>True if the file carries an optional 24-bit sample extension (<c>sm24</c>).</summary>
    public bool Has24BitSamples => _sdta.Has24BitData;

    /// <summary>
    /// The entire <c>smpl</c> chunk as 16-bit PCM frames. Indexed by absolute frame number;
    /// <see cref="SampleHeader.Start"/> and <see cref="SampleHeader.End"/> index into this span.
    /// Zero-copy on little-endian platforms. Any 24-bit extension is ignored here — for
    /// 24-bit-aware decoding use <see cref="GetSampleData(int,int,int,int)"/>.
    /// </summary>
    public ReadOnlySpan<short> RawSampleData => _sdta.GetAllSamples16();

    /// <summary>
    /// Raw bytes of the <c>smpl</c> chunk. For SF2 this is the same data as
    /// <see cref="RawSampleData"/> in byte form. For SF3 it's a sequence of Vorbis
    /// bitstreams whose start/end byte offsets are in each
    /// <see cref="SampleHeader.Start"/> / <see cref="SampleHeader.End"/>.
    /// </summary>
    public ReadOnlyMemory<byte> RawSampleBytes => _sdta.RawBytes;

    /// <summary>
    /// The optional <c>sm24</c> chunk (one LS byte per frame) for 24-bit SF2 fonts, aligned 1:1 with
    /// <see cref="RawSampleBytes"/>. Empty for 16-bit fonts and SF3 (whose samples are Vorbis). The
    /// sample source combines this with the 16-bit data to play back at full 24-bit precision.
    /// </summary>
    public ReadOnlyMemory<byte> RawSample24Bytes => _sdta.RawBytes24;

    private SoundFont(InfoMetadata info, Preset[] presets, Instrument[] instruments,
        SampleHeader[] sampleHeaders, SdtaChunkReader sdta)
    {
        Info = info;
        Presets = presets;
        Instruments = instruments;
        _allSampleHeaders = sampleHeaders;
        _sdta = sdta;
    }

    // ----- Loading -------------------------------------------------------

    /// <summary>Load a SoundFont from a file path.</summary>
    public static SoundFont Load(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new SoundFontException(SoundFontValidationCode.BadFileName);
        if (!File.Exists(path))
            throw new SoundFontException(SoundFontValidationCode.BadFileName, $"File not found: {path}");
        return Load(File.ReadAllBytes(path));
    }

    /// <summary>Load a SoundFont from a byte array.</summary>
    public static SoundFont Load(byte[] data) => Load(new ReadOnlyMemory<byte>(data));

    /// <summary>Load a SoundFont from a <see cref="ReadOnlyMemory{T}"/>.</summary>
    public static SoundFont Load(ReadOnlyMemory<byte> data)
    {
        var riff = new RiffReader(data);
        InfoMetadata info = InfoChunkReader.Read(riff.Info);
        var sdta = new SdtaChunkReader(riff.Sdta);
        var pdta = new PdtaChunkReader(riff.Pdta);

        PresetHeaderRecord[] phdrs = Parsers.ParsePhdr(pdta.Phdr);
        BagRecord[] pbags = Parsers.ParseBag(pdta.Pbag);
        Modulator[] pmods = Parsers.ParseMod(pdta.Pmod);
        Generator[] pgens = Parsers.ParseGen(pdta.Pgen);
        InstrumentRecord[] insts = Parsers.ParseInst(pdta.Inst);
        BagRecord[] ibags = Parsers.ParseBag(pdta.Ibag);
        Modulator[] imods = Parsers.ParseMod(pdta.Imod);
        Generator[] igens = Parsers.ParseGen(pdta.Igen);
        SampleHeader[] shdrs = Parsers.ParseShdr(pdta.Shdr);

        ValidatePresets(phdrs, pbags, pgens, pmods);
        ValidateInstruments(insts, ibags, igens, imods);

        Preset[] presets = BuildPresets(phdrs, pbags, pgens, pmods);
        Instrument[] instruments = BuildInstruments(insts, ibags, igens, imods, shdrs);

        // Strip terminal EOS sample header.
        SampleHeader[] realSamples = shdrs.Where(s => s.Name != "EOS").ToArray();

        // Compute EndOfRegion (next sample's start, or end of smpl buffer for the last).
        SampleHeader[] sortedByStart = realSamples.OrderBy(s => s.Start).ToArray();
        for (var i = 0; i < sortedByStart.Length - 1; i++)
            sortedByStart[i].EndOfRegion = sortedByStart[i + 1].Start;
        if (sortedByStart.Length > 0)
            sortedByStart[^1].EndOfRegion = (uint)sdta.FrameCount + 1;

        // Strip terminal EOP/EOI from public lists.
        Preset[] presetList = presets.Where(p => p.Name != "EOP").ToArray();
        Instrument[] instrumentList = instruments.Where(i => i.Name != "EOI").ToArray();

        return new SoundFont(info, presetList, instrumentList, realSamples, sdta);
    }

    // ----- Validation pipeline (ports the checks in SFont::loadFont) ---

    private static void ValidatePresets(PresetHeaderRecord[] phdrs, BagRecord[] pbags,
        Generator[] pgens, Modulator[] pmods)
    {
        // BagIndex must be non-decreasing. Equal values are allowed: that means the preset
        // has zero zones, which is unusual but valid per SF2 §7.2.
        for (var x = 0; x < phdrs.Length - 1; x++)
            if (phdrs[x].BagIndex > phdrs[x + 1].BagIndex)
                throw new SoundFontException(SoundFontValidationCode.PresetNdxNonMonotonic);

        if (pbags.Length < phdrs[^1].BagIndex)
            throw new SoundFontException(SoundFontValidationCode.PbagCountBad);

        for (var x = 0; x < pbags.Length - 1; x++)
        {
            if (pbags[x].GenIndex > pbags[x + 1].GenIndex)
                throw new SoundFontException(SoundFontValidationCode.PbagGenNdxNonMonotonic);
            if (pbags[x].ModIndex > pbags[x + 1].ModIndex)
                throw new SoundFontException(SoundFontValidationCode.PbagModNdxNonMonotonic);
        }

        if (pgens.Length < pbags[^1].GenIndex)
            throw new SoundFontException(SoundFontValidationCode.PbagGenCountBad);
        if (pmods.Length < pbags[^1].ModIndex)
            throw new SoundFontException(SoundFontValidationCode.PbagModCountBad);
    }

    private static void ValidateInstruments(InstrumentRecord[] insts, BagRecord[] ibags,
        Generator[] igens, Modulator[] imods)
    {
        // Same relaxation as ValidatePresets — zero-zone instruments are spec-legal.
        for (var x = 0; x < insts.Length - 1; x++)
            if (insts[x].BagIndex > insts[x + 1].BagIndex)
                throw new SoundFontException(SoundFontValidationCode.InstNdxNonMonotonic);

        if (ibags.Length < insts[^1].BagIndex)
            throw new SoundFontException(SoundFontValidationCode.IbagCountBad);

        for (var x = 0; x < ibags.Length - 1; x++)
        {
            if (ibags[x].GenIndex > ibags[x + 1].GenIndex)
                throw new SoundFontException(SoundFontValidationCode.IbagGenNdxNonMonotonic);
            if (ibags[x].ModIndex > ibags[x + 1].ModIndex)
                throw new SoundFontException(SoundFontValidationCode.IbagModNdxNonMonotonic);
        }

        if (igens.Length < ibags[^1].GenIndex)
            throw new SoundFontException(SoundFontValidationCode.IbagGenCountBad);
        if (imods.Length < ibags[^1].ModIndex)
            throw new SoundFontException(SoundFontValidationCode.IbagModCountBad);
    }

    // ----- Build typed model ------------------------------------------

    private static Preset[] BuildPresets(PresetHeaderRecord[] phdrs, BagRecord[] pbags,
        Generator[] pgens, Modulator[] pmods)
    {
        // The final phdr record is the terminal "EOP" sentinel (SF2 §7.2): it
        // exists only to bound the previous preset's zone range and is not itself
        // a preset. Build N-1 presets, excluding it by position — a name-based
        // strip alone misses terminals with a blank/non-standard name (e.g.
        // FluidR3Mono, whose empty-named terminal would otherwise shadow the real
        // bank-0/program-0 preset). header[i+1].BagIndex bounds preset i's zones.
        var presets = new Preset[phdrs.Length - 1];
        for (var i = 0; i < phdrs.Length - 1; i++)
        {
            PresetHeaderRecord ph = phdrs[i];
            presets[i] = new Preset
            {
                Name = ph.Name,
                Number = ph.Preset,
                Bank = ph.Bank,
                Library = ph.Library,
                Genre = ph.Genre,
                Morphology = ph.Morphology,
            };
        }

        for (var i = 0; i < phdrs.Length - 1; i++)
        {
            int bagStart = phdrs[i].BagIndex;
            int bagEnd = phdrs[i + 1].BagIndex;
            var zoneIndex = 0;
            for (int b = bagStart; b < bagEnd; b++)
            {
                var zone = new Zone { Index = zoneIndex++ };
                int genStart = pbags[b].GenIndex;
                int genEnd = b + 1 < pbags.Length ? pbags[b + 1].GenIndex : pgens.Length;
                for (int g = genStart; g < genEnd; g++)
                    zone.Generators.Add(pgens[g]);
                int modStart = pbags[b].ModIndex;
                int modEnd = b + 1 < pbags.Length ? pbags[b + 1].ModIndex : pmods.Length;
                for (int m = modStart; m < modEnd; m++)
                    zone.Modulators.Add(pmods[m]);

                // Trailing 'instrument' generator becomes the zone's InstrumentIndex.
                if (zone.Generators.Count > 0 && zone.Generators[^1].Operator == SFGenerator.Instrument)
                {
                    zone.InstrumentIndex = zone.Generators[^1].Amount.Word;
                    zone.RemoveLastGenerator();
                }
                presets[i].Zones.Add(zone);
            }
        }
        return presets;
    }

    private static Instrument[] BuildInstruments(InstrumentRecord[] insts, BagRecord[] ibags,
        Generator[] igens, Modulator[] imods, SampleHeader[] shdrs)
    {
        var instruments = new Instrument[insts.Length];
        for (var i = 0; i < insts.Length; i++)
            instruments[i] = new Instrument { Name = insts[i].Name, Index = i };

        for (var i = 0; i < insts.Length - 1; i++)
        {
            int bagStart = insts[i].BagIndex;
            int bagEnd = insts[i + 1].BagIndex;
            var zoneIndex = 0;
            for (int b = bagStart; b < bagEnd; b++)
            {
                var zone = new Zone { Index = zoneIndex++ };
                int genStart = ibags[b].GenIndex;
                int genEnd = b + 1 < ibags.Length ? ibags[b + 1].GenIndex : igens.Length;
                for (int g = genStart; g < genEnd; g++)
                    zone.Generators.Add(igens[g]);
                int modStart = ibags[b].ModIndex;
                int modEnd = b + 1 < ibags.Length ? ibags[b + 1].ModIndex : imods.Length;
                for (int m = modStart; m < modEnd; m++)
                    zone.Modulators.Add(imods[m]);

                // Trailing 'sampleID' generator resolves to a SampleHeader.
                if (zone.Generators.Count > 0 && zone.Generators[^1].Operator == SFGenerator.SampleID)
                {
                    ushort idx = zone.Generators[^1].Amount.Word;
                    if (idx < shdrs.Length)
                    {
                        zone.Sample = shdrs[idx];
                        zone.Sample.OriginalIndex = idx;
                    }
                    zone.RemoveLastGenerator();
                }
                instruments[i].Zones.Add(zone);
            }
        }
        return instruments;
    }

    // ----- Query API (idiomatic replacements for the C++ accessors) ---

    /// <summary>Distinct, sorted list of bank numbers that have presets.</summary>
    public IReadOnlyList<int> Banks =>
        Presets.Select(p => (int)p.Bank).Distinct().OrderBy(b => b).ToArray();

    /// <summary>Presets that belong to a specific bank, sorted by preset number.</summary>
    public IReadOnlyList<Preset> PresetsInBank(int bank) =>
        Presets.Where(p => p.Bank == bank).OrderBy(p => p.Number).ToArray();

    /// <summary>Looks up a preset by (bank, preset). Returns null if no match.</summary>
    public Preset? FindPreset(int bank, int preset) =>
        Presets.FirstOrDefault(p => p.Bank == bank && p.Number == preset);

    /// <summary>Number of zones in the named preset (0 if not found).</summary>
    public int PresetZoneCount(int bank, int preset) =>
        FindPreset(bank, preset)?.Zones.Count ?? 0;

    /// <summary>Returns the instrument referenced by a particular preset zone, or null.</summary>
    public Instrument? GetZoneInstrument(int bank, int preset, int zoneIndex)
    {
        Preset? p = FindPreset(bank, preset);
        if (p is null || zoneIndex >= p.Zones.Count) return null;
        int instIdx = p.Zones[zoneIndex].InstrumentIndex;
        return instIdx >= 0 && instIdx < Instruments.Count ? Instruments[instIdx] : null;
    }

    /// <summary>True if a preset zone references an instrument.</summary>
    public bool HasInstrument(int bank, int preset, int zoneIndex) =>
        GetZoneInstrument(bank, preset, zoneIndex) is not null;

    /// <summary>True if the zone path resolves to a sample.</summary>
    public bool HasSample(int bank, int preset, int presetZone, int instrumentZone)
    {
        Instrument? inst = GetZoneInstrument(bank, preset, presetZone);
        if (inst is null || instrumentZone >= inst.Zones.Count) return false;
        return inst.Zones[instrumentZone].Sample is not null;
    }

    /// <summary>Returns a sample-info view (offsets rebased to zero) for a zone path.</summary>
    public SampleInfo? GetSampleInfo(int bank, int preset, int presetZone, int instrumentZone)
    {
        Instrument? inst = GetZoneInstrument(bank, preset, presetZone);
        if (inst is null || instrumentZone >= inst.Zones.Count) return null;
        SampleHeader? s = inst.Zones[instrumentZone].Sample;
        if (s is null) return null;
        return new SampleInfo
        {
            Name = s.Name,
            Start = 0,
            End = s.End - s.Start,
            StartLoop = s.StartLoop - s.Start,
            EndLoop = s.EndLoop - s.Start,
            SampleRate = s.SampleRate,
            OriginalPitch = s.OriginalPitch,
            PitchCorrection = s.PitchCorrection,
            SampleLink = s.SampleLink,
            SampleType = s.SampleType,
        };
    }

    /// <summary>Returns decoded sample frames (promoted to 24-bit if sm24 is present).</summary>
    public int[] GetSampleData(int bank, int preset, int presetZone, int instrumentZone)
    {
        Instrument? inst = GetZoneInstrument(bank, preset, presetZone);
        if (inst is null || instrumentZone >= inst.Zones.Count) return [];
        SampleHeader? s = inst.Zones[instrumentZone].Sample;
        if (s is null) return [];
        return _sdta.GetSamples(s.Start, s.End);
    }

    /// <summary>Returns the generator list for each zone of the named preset (keyed by zone index).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<Generator>> GetPresetGenerators(int bank, int preset)
    {
        Preset? p = FindPreset(bank, preset);
        if (p is null) return new Dictionary<int, IReadOnlyList<Generator>>();
        return p.Zones.ToDictionary(z => z.Index, z => (IReadOnlyList<Generator>)z.Generators);
    }

    /// <summary>Returns the modulator list for each zone of the named preset (keyed by zone index).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<Modulator>> GetPresetModulators(int bank, int preset)
    {
        Preset? p = FindPreset(bank, preset);
        if (p is null) return new Dictionary<int, IReadOnlyList<Modulator>>();
        return p.Zones.ToDictionary(z => z.Index, z => (IReadOnlyList<Modulator>)z.Modulators);
    }

    /// <summary>Returns generators per instrument zone for the instrument linked from a preset zone.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<Generator>> GetInstrumentGenerators(int bank, int preset, int presetZone)
    {
        Instrument? inst = GetZoneInstrument(bank, preset, presetZone);
        if (inst is null) return new Dictionary<int, IReadOnlyList<Generator>>();
        return inst.Zones.ToDictionary(z => z.Index, z => (IReadOnlyList<Generator>)z.Generators);
    }

    /// <summary>Returns modulators per instrument zone for the instrument linked from a preset zone.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<Modulator>> GetInstrumentModulators(int bank, int preset, int presetZone)
    {
        Instrument? inst = GetZoneInstrument(bank, preset, presetZone);
        if (inst is null) return new Dictionary<int, IReadOnlyList<Modulator>>();
        return inst.Zones.ToDictionary(z => z.Index, z => (IReadOnlyList<Modulator>)z.Modulators);
    }

    // ----- Extract --------------------------------------------------

    /// <summary>
    /// Repackages a single preset (identified by bank+preset) into a new, self-contained SF2 byte array.
    /// Returns an empty array if the preset cannot be extracted (e.g. has no samples, has invalid
    /// sample bounds, or the combined output would exceed 2 GB and overflow an in-memory byte array).
    /// </summary>
    public byte[] ExtractPreset(int bank, int preset)
    {
        Preset? p = FindPreset(bank, preset);
        if (p is null) return [];
        try
        {
            return new PresetExtractor(this, _sdta).Extract(p);
        }
        catch (Exception ex) when (ex is IOException or OutOfMemoryException or OverflowException)
        {
            return [];
        }
    }

    internal IReadOnlyList<Instrument> InstrumentsInternal => Instruments;

    /// <summary>
    /// Validates each sample header against SF2 spec §6.1 / §7.10. Returns one issue per
    /// defect; a single sample may produce multiple issues. An empty list means every
    /// sample header is structurally sound.
    /// </summary>
    public IReadOnlyList<SampleValidationIssue> ValidateSamples()
    {
        var issues = new List<SampleValidationIssue>();
        int frameCount = _sdta.FrameCount;

        for (var i = 0; i < _allSampleHeaders.Length; i++)
        {
            SampleHeader s = _allSampleHeaders[i];

            void Add(SampleValidationCode code, string msg) =>
                issues.Add(new SampleValidationIssue(i, s.Name, code, msg));

            // --- Range / size ---
            if (s.End <= s.Start)
                Add(SampleValidationCode.StartNotBeforeEnd,
                    $"Start={s.Start}, End={s.End}");
            else
            {
                if (s.End > frameCount)
                    Add(SampleValidationCode.SampleExceedsSmpl,
                        $"End={s.End} > smpl frame count {frameCount}");

                uint length = s.End - s.Start;
                if (length < 48)
                    Add(SampleValidationCode.SampleTooShort,
                        $"length={length} frames (spec minimum is 48)");
            }

            // --- Loop ---
            if (s.StartLoop < s.Start)
                Add(SampleValidationCode.LoopStartBeforeSampleStart,
                    $"StartLoop={s.StartLoop} < Start={s.Start}");
            if (s.EndLoop > s.End)
                Add(SampleValidationCode.LoopEndAfterSampleEnd,
                    $"EndLoop={s.EndLoop} > End={s.End}");
            if (s.EndLoop <= s.StartLoop)
                Add(SampleValidationCode.LoopStartNotBeforeLoopEnd,
                    $"StartLoop={s.StartLoop}, EndLoop={s.EndLoop}");
            else
            {
                if (s.EndLoop - s.StartLoop < 32)
                    Add(SampleValidationCode.LoopTooShort,
                        $"loop length={s.EndLoop - s.StartLoop} (spec minimum is 32)");
            }

            if (s.StartLoop >= s.Start && s.StartLoop - s.Start < 8)
                Add(SampleValidationCode.LoopStartMarginTooSmall,
                    $"StartLoop-Start={s.StartLoop - s.Start} (spec minimum is 8)");
            if (s.EndLoop <= s.End && s.End - s.EndLoop < 8)
                Add(SampleValidationCode.LoopEndMarginTooSmall,
                    $"End-EndLoop={s.End - s.EndLoop} (spec minimum is 8)");

            // --- Sample rate / pitch ---
            if (s.SampleRate is < 400 or > 50000)
                Add(SampleValidationCode.SampleRateOutOfRange,
                    $"SampleRate={s.SampleRate} (spec range is 400..50000)");
            if (s.OriginalPitch > 127 && s.OriginalPitch != 255)
                Add(SampleValidationCode.OriginalPitchInvalid,
                    $"OriginalPitch={s.OriginalPitch} (spec allows 0..127 or 255)");

            // --- Stereo linkage ---
            bool isStereo = s.SampleType is SFSampleLink.LeftSample or SFSampleLink.RightSample
                              or SFSampleLink.RomLeftSample or SFSampleLink.RomRightSample;
            if (isStereo)
            {
                if (s.SampleLink >= _allSampleHeaders.Length)
                {
                    Add(SampleValidationCode.StereoLinkInvalid,
                        $"SampleLink={s.SampleLink} (only {_allSampleHeaders.Length} samples)");
                }
                else
                {
                    SampleHeader partner = _allSampleHeaders[s.SampleLink];
                    SFSampleLink expectedPartnerType = s.SampleType switch
                    {
                        SFSampleLink.LeftSample => SFSampleLink.RightSample,
                        SFSampleLink.RightSample => SFSampleLink.LeftSample,
                        SFSampleLink.RomLeftSample => SFSampleLink.RomRightSample,
                        SFSampleLink.RomRightSample => SFSampleLink.RomLeftSample,
                        _ => s.SampleType,
                    };
                    if (partner.SampleType != expectedPartnerType)
                        Add(SampleValidationCode.StereoLinkTypeMismatch,
                            $"partner '{partner.Name}' has type {partner.SampleType}, expected {expectedPartnerType}");
                    else if (partner.End - partner.Start != s.End - s.Start)
                        Add(SampleValidationCode.StereoLinkLengthMismatch,
                            $"partner length {partner.End - partner.Start} != this length {s.End - s.Start}");
                }
            }
        }

        return issues;
    }
}
