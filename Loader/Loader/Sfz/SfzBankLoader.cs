using System;
using System.Collections.Generic;
using System.IO;
using MidiSharp.Audio;

namespace MidiSharp.SoundBank.Sfz;

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
    public static SoundBank Load(string path, SoundBankLoadOptions options)
    {
        var acc = new Accumulator();
        acc.AddFile(path, bank: 0);
        return acc.Build(Path.GetFileNameWithoutExtension(path));
    }

    public static SoundBank Load(Stream stream, string? basePath, SoundBankLoadOptions options)
    {
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        string baseDir = basePath != null
            ? (Path.GetDirectoryName(Path.GetFullPath(basePath)) ?? ".")
            : ".";
        string name = basePath != null ? Path.GetFileNameWithoutExtension(basePath) : "SFZ Instrument";

        var acc = new Accumulator();
        acc.AddText(text, baseDir, bank: 0);
        return acc.Build(name);
    }

    /// <summary>
    /// Load several SFZ files into one bank, each on its own MIDI bank number.
    /// Samples are pooled and de-duplicated across all files, so a zone's sample
    /// id is global and needs no remapping.
    /// </summary>
    public static SoundBank LoadCombined(IReadOnlyList<(string Path, int Bank)> files, SoundBankLoadOptions options)
    {
        if (files == null || files.Count == 0)
            throw new SoundBankLoadException("No SFZ files provided");

        var acc = new Accumulator();
        foreach (var (path, bank) in files)
            acc.AddFile(path, bank);
        return acc.Build(Path.GetFileNameWithoutExtension(files[0].Path));
    }

    /// <summary>
    /// Mutable load state shared across one or more SFZ files: the decoded sample
    /// pool, a path→id map for de-duplication, and every translated zone tagged
    /// with the (bank, program-range) it occupies.
    /// </summary>
    private sealed class Accumulator
    {
        private readonly List<(DecodedAudio Audio, string Name)> _samples = new();
        private readonly Dictionary<string, int> _idByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(int Bank, int Lo, int Hi, PatchZone Zone)> _placed = new();
        private int _missing;

        public void AddFile(string path, int bank)
        {
            string text = File.ReadAllText(path);
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            AddText(text, baseDir, bank);
        }

        public void AddText(string text, string baseDir, int bank)
        {
            var instrument = SfzParser.Parse(text, inc => ReadInclude(baseDir, inc));

            foreach (var region in instrument.Regions)
            {
                string? sampleValue = region.Get("sample");
                if (string.IsNullOrWhiteSpace(sampleValue)) continue;

                string? resolved = ResolveSamplePath(baseDir, instrument.Control.DefaultPath, sampleValue!);
                if (resolved == null) { _missing++; continue; }

                if (!_idByPath.TryGetValue(resolved, out int sampleId))
                {
                    DecodedAudio audio;
                    try { audio = AudioCodecs.Decode(resolved); }
                    catch { _missing++; continue; }
                    sampleId = _samples.Count;
                    _samples.Add((audio, Path.GetFileNameWithoutExtension(resolved)));
                    _idByPath[resolved] = sampleId;
                }

                int lo = Math.Clamp(region.GetInt("loprog", 0), 0, 127);
                int hi = Math.Clamp(region.GetInt("hiprog", 127), 0, 127);
                _placed.Add((bank, lo, hi,
                    SfzZoneTranslator.Build(region, instrument.Control, sampleId, _samples[sampleId].Audio)));
            }
        }

        public SoundBank Build(string name)
        {
            if (_placed.Count == 0)
                throw new SoundBankLoadException(
                    $"SFZ '{name}' produced no playable regions" +
                    (_missing > 0 ? $" ({_missing} sample reference(s) could not be resolved/decoded)" : ""));

            // Group zones into patches by (bank, program). loprog/hiprog give each
            // region's program span; the document order of zones within a patch is
            // preserved (it matters for overlapping / round-robin / keyswitch zones).
            var byKey = new Dictionary<(int Bank, int Program), List<PatchZone>>();
            foreach (var (bank, lo, hi, zone) in _placed)
            {
                for (int program = lo; program <= hi; program++)
                {
                    var key = (bank, program);
                    if (!byKey.TryGetValue(key, out var list))
                        byKey[key] = list = new List<PatchZone>();
                    list.Add(zone);
                }
            }

            var patches = new List<Patch>(byKey.Count);
            foreach (var kv in byKey)
                patches.Add(new Patch
                {
                    Bank = kv.Key.Bank,
                    Program = kv.Key.Program,
                    Name = name,
                    Zones = kv.Value,
                });

            return new SoundBank
            {
                Name = name,
                SourceFormat = SoundBankFormat.Sfz,
                Patches = patches,
                Samples = new SfzSampleSource(_samples),
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
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
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
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
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
