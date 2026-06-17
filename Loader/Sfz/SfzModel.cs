using System;
using System.Collections.Generic;
using System.Globalization;

namespace Loader.Sfz;

/// <summary>
/// Settings from the SFZ <c>&lt;control&gt;</c> header that affect how every
/// region is interpreted: where samples live and how key numbers are offset.
/// </summary>
internal sealed class SfzControl
{
    /// <summary>Prefix prepended to every <c>sample=</c> path (relative to the .sfz dir).</summary>
    public string DefaultPath { get; set; } = string.Empty;

    /// <summary>Added to every key opcode as <c>octave * 12</c>.</summary>
    public int OctaveOffset { get; set; }

    /// <summary>Added to every key opcode (semitones).</summary>
    public int NoteOffset { get; set; }

    /// <summary>Total semitone offset applied to lokey/hikey/pitch_keycenter/sw_*.</summary>
    public int KeyOffset => OctaveOffset * 12 + NoteOffset;

    /// <summary>
    /// Initial controller values from <c>set_ccN</c> (0..127) / <c>set_hdccN</c> (0..1 → 0..127),
    /// keyed by CC number. Seeded into the synth's channel state when the bank loads so CC-driven
    /// routes and locc/hicc gates start where the instrument expects.
    /// </summary>
    public Dictionary<int, int> InitialControllers { get; } = new();
}

/// <summary>
/// One fully-flattened SFZ region: the merged opcode set after cascading
/// <c>&lt;global&gt; → &lt;master&gt; → &lt;group&gt; → &lt;region&gt;</c>.
/// Opcode lookups are case-insensitive (the spec treats opcode names as
/// lowercase; values keep their original case for paths).
/// </summary>
internal sealed class SfzRegion
{
    private readonly Dictionary<string, string> _opcodes;

    public SfzRegion(Dictionary<string, string> opcodes) => _opcodes = opcodes;

    public bool Has(string key) => _opcodes.ContainsKey(key);

    public string? Get(string key) => _opcodes.TryGetValue(key, out var v) ? v : null;

    public IReadOnlyDictionary<string, string> Opcodes => _opcodes;

    public int GetInt(string key, int fallback)
    {
        var v = Get(key);
        return v != null && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : fallback;
    }

    public double GetDouble(string key, double fallback)
    {
        var v = Get(key);
        return v != null && double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n : fallback;
    }

    /// <summary>Parse a key opcode (number or note name) and apply the control offset.</summary>
    public int? GetKey(string key, SfzControl control)
    {
        var v = Get(key);
        if (v == null || !SfzNoteNames.TryParse(v, out int midi)) return null;
        return Math.Clamp(midi + control.KeyOffset, 0, 127);
    }

    /// <summary>
    /// Enumerate the (controllerNumber, value) pairs for opcodes of the shape
    /// <c>{prefix}{N}</c> — e.g. <c>locc74=20</c> with prefix <c>locc</c> yields
    /// (74, "20"). Used for the open-ended <c>locc/hicc/on_locc/on_hicc</c> sets.
    /// </summary>
    public IEnumerable<(int Cc, string Value)> EnumerateCc(string prefix)
    {
        foreach (var kv in _opcodes)
        {
            if (kv.Key.Length > prefix.Length &&
                kv.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(kv.Key.Substring(prefix.Length), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out int cc) &&
                cc is >= 0 and <= 127)
            {
                yield return (cc, kv.Value);
            }
        }
    }

    /// <summary>
    /// Enumerate CC-modulation opcodes of the shape <c>{param}_oncc{N}</c> or the
    /// legacy <c>{param}_cc{N}</c> — e.g. <c>pan_oncc10=200</c> yields
    /// ("pan", 10, 200). These define how a continuous controller modulates a
    /// parameter; the translator maps the ones it supports to modulation routes.
    /// </summary>
    public IEnumerable<(string Param, int Cc, double Value)> EnumerateModulations()
    {
        foreach (var kv in _opcodes)
        {
            string key = kv.Key;
            int marker = key.IndexOf("_oncc", StringComparison.Ordinal);
            int markerLen = 5;
            if (marker < 0) { marker = key.IndexOf("_cc", StringComparison.Ordinal); markerLen = 3; }
            if (marker <= 0) continue;

            string ccPart = key.Substring(marker + markerLen);
            if (!int.TryParse(ccPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cc))
                continue;  // not a numeric-CC suffix (e.g. *_curvecc, *_smoothcc handled elsewhere)
            if (!double.TryParse(kv.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                continue;

            yield return (key.Substring(0, marker), cc, val);
        }
    }
}

/// <summary>The result of parsing an SFZ file: control settings + flat regions.</summary>
internal sealed class SfzInstrument
{
    public SfzControl Control { get; init; } = new();
    public IReadOnlyList<SfzRegion> Regions { get; init; } = Array.Empty<SfzRegion>();

    /// <summary>
    /// Non-cascading headers the parser skipped (e.g. <c>curve</c>, <c>effect</c>,
    /// <c>sample</c>), as header-name → occurrence count. Their opcodes are dropped;
    /// this records that they were present so a diagnostic can surface them.
    /// </summary>
    public IReadOnlyDictionary<string, int> IgnoredHeaders { get; init; }
        = new Dictionary<string, int>();

    /// <summary>
    /// <c>&lt;control&gt;</c> opcodes the parser didn't act on (e.g. ARIA <c>set_ccN</c>,
    /// <c>label_ccN</c>, <c>hint_*</c>), as opcode → occurrence count. These never reach a
    /// region, so the diagnostic relies on this to surface them.
    /// </summary>
    public IReadOnlyDictionary<string, int> ControlIgnored { get; init; }
        = new Dictionary<string, int>();
}
