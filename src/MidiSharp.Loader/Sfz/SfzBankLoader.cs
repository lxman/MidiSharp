using System;
using System.Collections.Generic;
using System.IO;
using MidiSharp.Audio;
using MidiSharp.SoundBank;
using IRBank = MidiSharp.SoundBank.SoundBank;
namespace MidiSharp.Loader.Sfz;

/// <summary>
/// SFZ → IR translator. An SFZ file describes a single instrument as text opcodes
/// referencing external sample files. We parse the opcode hierarchy, decode each
/// referenced file once, and translate every region into a <see cref="PatchZone"/>.
/// </summary>
/// <remarks>
/// A single file's regions land on bank 0 (across the programs their loprog/hiprog
/// span — or all 128 if unspecified, since an SFZ has no program of its own).
/// Several files can be combined into one bank via <see cref="LoadCombined"/> —
/// used to place a GM bank's drum kit on bank 128 (the synth routes MIDI channel
/// 10 there) alongside the melodic instruments on bank 0, sharing one sample pool.
/// </remarks>
internal static class SfzBankLoader
{
    public static IRBank Load(string path, SoundBankLoadOptions options)
    {
        var acc = new Accumulator();
        acc.AddFile(path, bank: 0);
        return acc.Build(Path.GetFileNameWithoutExtension(path), options.DecodedSampleCacheBytes, options.BlockingSampleDecode);
    }

    public static IRBank Load(Stream stream, string? basePath, SoundBankLoadOptions options)
    {
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        string baseDir = basePath != null
            ? (Path.GetDirectoryName(Path.GetFullPath(basePath)) ?? ".")
            : ".";
        string? name = basePath != null ? Path.GetFileNameWithoutExtension(basePath) : "SFZ Instrument";

        var acc = new Accumulator();
        acc.AddText(text, baseDir, bank: 0);
        return acc.Build(name, options.DecodedSampleCacheBytes, options.BlockingSampleDecode);
    }

    /// <summary>
    /// Load several SFZ files into one bank, each on its own MIDI bank number.
    /// Samples are pooled and de-duplicated across all files, so a zone's sample
    /// id is global and needs no remapping.
    /// </summary>
    public static IRBank LoadCombined(IReadOnlyList<(string Path, int Bank)> files, SoundBankLoadOptions options)
    {
        if (files == null || files.Count == 0)
            throw new SoundBankLoadException("No SFZ files provided");

        var acc = new Accumulator();
        foreach ((string path, int bank) in files)
            acc.AddFile(path, bank);
        return acc.Build(Path.GetFileNameWithoutExtension(files[0].Path), options.DecodedSampleCacheBytes, options.BlockingSampleDecode);
    }

    /// <summary>
    /// Mutable load state shared across one or more SFZ files: the decoded sample
    /// pool, a path→id map for de-duplication, and every translated zone tagged
    /// with the (bank, program-range) it occupies.
    /// </summary>
    private sealed class Accumulator
    {
        private readonly List<string> _paths = [];
        private readonly List<AudioInfo> _infos = [];
        private readonly List<SampleMetadata> _metadatas = [];
        private readonly Dictionary<string, int> _idByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(int Bank, int Lo, int Hi, PatchZone Zone, string? Label)> _placed = [];
        private readonly Dictionary<int, int> _initialControllers = new();   // set_ccN/set_hdccN, CC# → 0..127
        private int _missing;

        public void AddFile(string path, int bank)
        {
            string text = File.ReadAllText(path);
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            AddText(text, baseDir, bank);
        }

        public void AddText(string text, string baseDir, int bank)
        {
            SfzInstrument instrument = SfzParser.Parse(text, inc => ReadInclude(baseDir, inc));

            // Carry the <control> set_cc/set_hdcc seeds (later files win on conflict).
            foreach (KeyValuePair<int, int> kv in instrument.Control.InitialControllers)
                _initialControllers[kv.Key] = kv.Value;

            foreach (SfzRegion? region in instrument.Regions)
            {
                string? sampleValue = region.Get("sample");
                if (string.IsNullOrWhiteSpace(sampleValue)) continue;
                sampleValue = sampleValue!.Trim();

                int sampleId;
                if (sampleValue.StartsWith("*", StringComparison.Ordinal))
                {
                    // ARIA built-in generator (*sine, *silence, …): no file ships, so register a
                    // silent placeholder keyed by the token. Keeps the region — and its program
                    // slot — loadable instead of being dropped as a missing sample.
                    if (!_idByPath.TryGetValue(sampleValue, out sampleId))
                    {
                        AudioInfo info = GeneratorInfo();
                        sampleId = _paths.Count;
                        _paths.Add(sampleValue);   // the "*…" token doubles as the source path
                        _infos.Add(info);
                        _metadatas.Add(BuildMetadata(info, sampleValue));
                        _idByPath[sampleValue] = sampleId;
                    }
                }
                else
                {
                    // Resolve against the region's positional default_path (stamped by the parser).
                    string defaultPath = region.Get("default_path") ?? string.Empty;
                    string? resolved = ResolveSamplePath(baseDir, defaultPath, sampleValue);
                    if (resolved == null) { _missing++; continue; }

                    if (!_idByPath.TryGetValue(resolved, out sampleId))
                    {
                        // Peek the header only — the sample is decoded lazily on first play.
                        AudioInfo info;
                        try { info = AudioCodecs.Peek(resolved); }
                        catch { _missing++; continue; }
                        if (info.FrameCount <= 0) { _missing++; continue; }   // unreadable / empty header
                        sampleId = _paths.Count;
                        _paths.Add(resolved);
                        _infos.Add(info);
                        _metadatas.Add(BuildMetadata(info, Path.GetFileNameWithoutExtension(resolved)));
                        _idByPath[resolved] = sampleId;
                    }
                }

                int lo = Math.Clamp(region.GetInt("loprog", 0), 0, 127);
                int hi = Math.Clamp(region.GetInt("hiprog", 127), 0, 127);
                // Patch name from the program/instrument-level label only: master_label (Discord
                // labels its 128 GM slots this way) then global_label. group_label/region_label are
                // sub-group names (mic, velocity layer, articulation), not instrument names, so they
                // don't name the patch — it falls back to the filename instead.
                string? label = region.Get("master_label") ?? region.Get("global_label");
                _placed.Add((bank, lo, hi,
                    SfzZoneTranslator.Build(region, instrument.Control, sampleId, _infos[sampleId]), label));
            }
        }

        // Synthetic metadata for a built-in generator placeholder: one second of looping silence so
        // a held note sustains instead of cutting after the sample ends.
        private static AudioInfo GeneratorInfo() => new()
        {
            Channels = 1,
            SampleRate = 44100,
            FrameCount = 44100,
            RootKey = -1,
            LoopStartFrame = 0,
            LoopEndFrame = 44100,
        };

        // Sample metadata from a header peek — root key and loop fields fall back to SFZ opcodes
        // (filled by the zone translator) when the file header doesn't carry them.
        private static SampleMetadata BuildMetadata(AudioInfo info, string name) => new()
        {
            Name = name,
            SampleRate = info.SampleRate,
            // Stereo files are kept interleaved (2); mono and anything else fold to 1. The decoder
            // and the voice both read this to know how many floats make up a frame.
            Channels = info.Channels == 2 ? 2 : 1,
            LengthFrames = info.FrameCount,
            LoopStartFrames = info.HasLoop ? info.LoopStartFrame : 0,
            LoopEndFrames = info.HasLoop ? info.LoopEndFrame : info.FrameCount,
            RootKey = info.RootKey >= 0 ? info.RootKey : 60,
            PitchCorrectionCents = info.FineTuneCents,
        };

        public IRBank Build(string name, long cacheBudgetBytes, bool blockingDecode = false)
        {
            if (_placed.Count == 0)
                throw new SoundBankLoadException(
                    $"SFZ '{name}' produced no playable regions" +
                    (_missing > 0 ? $" ({_missing} sample reference(s) could not be resolved/decoded)" : ""));

            // Group zones into patches by (bank, program). loprog/hiprog give each
            // region's program span; the document order of zones within a patch is
            // preserved (it matters for overlapping / round-robin / keyswitch zones).
            var byKey = new Dictionary<(int Bank, int Program), List<PatchZone>>();
            var labelByKey = new Dictionary<(int Bank, int Program), string>();
            foreach ((int bank, int lo, int hi, PatchZone zone, string? label) in _placed)
            {
                for (int program = lo; program <= hi; program++)
                {
                    (int bank, int program) key = (bank, program);
                    if (!byKey.TryGetValue(key, out List<PatchZone>? list))
                        byKey[key] = list = [];
                    list.Add(zone);
                    if (label != null)
                        labelByKey.TryAdd(key, label);
                    // first label wins for this program
                }
            }

            var patches = new List<Patch>(byKey.Count);
            foreach (KeyValuePair<(int Bank, int Program), List<PatchZone>> kv in byKey)
                patches.Add(new Patch
                {
                    Bank = kv.Key.Bank,
                    Program = kv.Key.Program,
                    Name = labelByKey.TryGetValue(kv.Key, out string? lbl) ? lbl : name,
                    Zones = kv.Value,
                });

            // sustain_cc is per-region in SFZ but global to the pedal in practice; surface the dominant
            // non-default reassignment to the synth (half-pedal fonts route the whole bank to one CC).
            var sustainCc = 64;
            foreach ((int _, int _, int _, PatchZone zone, string? _) in _placed)
                if (zone.SustainCc != 64) { sustainCc = zone.SustainCc; break; }

            return new IRBank
            {
                Name = name,
                SourceFormat = SoundBankFormat.Sfz,
                Patches = patches,
                Samples = new SfzSampleSource(_paths, _metadatas, cacheBudgetBytes, blockingDecode),
                InitialControllers = _initialControllers,
                SustainCc = sustainCc,
            };
        }
    }

    // ── #include resolution ─────────────────────────────────────────────

    internal static string? ReadInclude(string baseDir, string includePath)
    {
        string? resolved = ResolveExisting(baseDir, includePath.Replace('\\', '/'));
        if (resolved == null) return null;
        try { return File.ReadAllText(resolved); }
        catch { return null; }
    }

    // ── Sample path resolution (default_path + case-insensitive fallback) ─

    private static string? ResolveSamplePath(string baseDir, string defaultPath, string sampleValue)
    {
        sampleValue = sampleValue.Replace('\\', '/').Trim();
        string relative = string.IsNullOrEmpty(defaultPath)
            ? sampleValue
            : defaultPath.TrimEnd('/') + "/" + sampleValue;
        return ResolveExisting(baseDir, relative);
    }

    /// <summary>
    /// Resolve <paramref name="relative"/> against <paramref name="baseDir"/>,
    /// first by exact path, then segment-by-segment case-insensitively (banks
    /// authored on Windows/macOS routinely mis-case paths for a Linux host).
    /// </summary>
    private static string? ResolveExisting(string baseDir, string relative)
    {
        string direct = Path.GetFullPath(Path.Combine(baseDir, relative));
        if (File.Exists(direct)) return direct;

        string current = baseDir;
        string[]? segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (seg == ".") continue;
            if (seg == "..") { current = Path.GetDirectoryName(current) ?? current; continue; }

            string candidate = Path.Combine(current, seg);
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                current = candidate;
                continue;
            }

            // Case-insensitive scan of the current directory for this segment.
            string? match = null;
            if (Directory.Exists(current))
            {
                foreach (string? entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (string.Equals(Path.GetFileName(entry), seg, StringComparison.OrdinalIgnoreCase))
                    {
                        match = entry;
                        break;
                    }
                }
            }
            if (match == null) return null;
            current = match;
        }

        return File.Exists(current) ? current : null;
    }
}
