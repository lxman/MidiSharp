using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MidiSharp.Loader.Sfz;

/// <summary>One unsupported opcode family and how many regions carry it.</summary>
public sealed record SfzOpcodeStat(string Opcode, int Count, string? Note);

/// <summary>A skipped non-cascading header and how many times it appeared.</summary>
public sealed record SfzHeaderStat(string Header, int Count);

/// <summary>
/// What an SFZ file uses that the loader does not act on — the visibility layer for
/// triaging ARIA-authored fonts. Parsing-only (no samples decoded), so it's cheap to run
/// across a whole collection. <see cref="HasFindings"/> is false for a fully-supported font.
/// </summary>
public sealed class SfzLoadReport
{
    /// <summary>The file name (without extension), or null when scanned from text.</summary>
    public string? Name { get; init; }

    /// <summary>Number of regions parsed.</summary>
    public int RegionCount { get; init; }

    /// <summary>Unsupported opcode families (numbered variants aggregated), most-used first.</summary>
    public IReadOnlyList<SfzOpcodeStat> UnsupportedOpcodes { get; init; } = Array.Empty<SfzOpcodeStat>();

    /// <summary>Skipped headers like <c>curve</c>/<c>effect</c>, most-frequent first.</summary>
    public IReadOnlyList<SfzHeaderStat> IgnoredHeaders { get; init; } = Array.Empty<SfzHeaderStat>();

    public bool HasFindings => UnsupportedOpcodes.Count > 0 || IgnoredHeaders.Count > 0;
}

/// <summary>
/// Reports which SFZ opcodes/headers a font uses that the loader silently ignores. The
/// authoritative "handled" set lives in <see cref="SfzOpcodes"/>; everything else is
/// reported here so an ARIA-extended font's dropped features are visible rather than silent.
/// </summary>
public static class SfzDiagnostics
{
    /// <summary>Parse <paramref name="path"/> (resolving <c>#include</c>s) and report what it drops. No samples decoded.</summary>
    public static SfzLoadReport Scan(string path)
    {
        string text = File.ReadAllText(path);
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var instrument = SfzParser.Parse(text, inc => SfzBankLoader.ReadInclude(baseDir, inc));
        return Analyze(instrument, Path.GetFileNameWithoutExtension(path));
    }

    internal static SfzLoadReport Analyze(SfzInstrument instrument, string? name)
    {
        // Count by region carrying the opcode (impact = zones affected), aggregating numbered
        // families so per-CC variants don't each take a row.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var region in instrument.Regions)
            foreach (var kv in region.Opcodes)
            {
                if (SfzOpcodes.IsHandled(kv.Key)) continue;
                string family = SfzOpcodes.Family(kv.Key);
                counts.TryGetValue(family, out int c);
                counts[family] = c + 1;
            }

        // <control>-scope opcodes never reach a region; fold them in (once each).
        foreach (var kv in instrument.ControlIgnored)
        {
            if (SfzOpcodes.IsHandled(kv.Key)) continue;
            string family = SfzOpcodes.Family(kv.Key);
            counts.TryGetValue(family, out int c);
            counts[family] = c + kv.Value;
        }

        var unsupported = counts
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new SfzOpcodeStat(kv.Key, kv.Value, SfzOpcodes.Describe(kv.Key)))
            .ToList();

        var headers = instrument.IgnoredHeaders
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new SfzHeaderStat(kv.Key, kv.Value))
            .ToList();

        return new SfzLoadReport
        {
            Name = name,
            RegionCount = instrument.Regions.Count,
            UnsupportedOpcodes = unsupported,
            IgnoredHeaders = headers,
        };
    }
}
