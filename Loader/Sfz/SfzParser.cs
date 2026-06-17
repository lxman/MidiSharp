using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Loader.Sfz;

/// <summary>
/// Parses SFZ text into a flat list of regions. Handles the parts of the SFZ
/// syntax real banks actually use: line (<c>//</c>) and block (<c>/* */</c>)
/// comments, <c>#define</c>/<c>#include</c> preprocessing, opcode values that
/// contain spaces (e.g. sample paths), and the
/// <c>&lt;global&gt;/&lt;master&gt;/&lt;group&gt;/&lt;region&gt;</c> cascade.
/// </summary>
/// <remarks>
/// Tokenization keys off anchors — a <c>&lt;header&gt;</c> or a <c>keyword=</c>.
/// An opcode's value is everything between its <c>=</c> and the next anchor,
/// which is how a value like <c>sample=Grand Piano C4.wav</c> keeps its spaces
/// without quoting. Headers and opcodes may share a line.
/// </remarks>
internal static class SfzParser
{
    private static readonly Regex Anchor =
        new(@"<[A-Za-z0-9_]+>|[A-Za-z0-9_]+=", RegexOptions.Compiled);

    private static readonly Regex Variable =
        new(@"\$[A-Za-z0-9_]+", RegexOptions.Compiled);

    // #define / #include can appear anywhere, including mid-line — real banks tack #include onto a
    // <master> line, and some (e.g. Headroom Piano) put "<region> #define $KEY 36 ... #include …" on
    // one line, so a later same-line #include must see the just-defined macro. We therefore process
    // both directives in a single document-order pass rather than two separate passes.
    private static readonly Regex Directive =
        new(@"#(?:define|include)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DefineTail =   // anchored just past "#define": $VAR then one token
        new(@"\G\s*(\$[A-Za-z0-9_]+)\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex IncludeTail =  // anchored just past "#include": "path"
        new(@"\G\s*""([^""]+)""", RegexOptions.Compiled);

    /// <param name="text">The .sfz file contents.</param>
    /// <param name="readInclude">
    /// Resolves an <c>#include "path"</c> to the included file's text (relative
    /// to the including file). Return null if the include can't be found —
    /// parsing continues without it.
    /// </param>
    public static SfzInstrument Parse(string text, Func<string, string?>? readInclude = null)
    {
        var defines = new Dictionary<string, string>(StringComparer.Ordinal);
        string expanded = Preprocess(text, readInclude, defines, depth: 0);
        return BuildRegions(expanded);
    }

    // ── Preprocessing: comments, #include, #define ──────────────────────

    private static string Preprocess(
        string text, Func<string, string?>? readInclude,
        Dictionary<string, string> defines, int depth)
    {
        text = StripComments(text);

        // Single document-order pass: emit non-directive text (with $var substitution), record each
        // #define as it's seen, and inline each #include at its position with the macros in effect so
        // far. This is what lets "<region> #define $KEY 36 … #include …" work — the #include sees $KEY.
        var sb = new StringBuilder(text.Length);
        int pos = 0;
        while (pos < text.Length)
        {
            var m = Directive.Match(text, pos);
            if (!m.Success)
            {
                sb.Append(Substitute(text.Substring(pos), defines));
                break;
            }

            sb.Append(Substitute(text.Substring(pos, m.Index - pos), defines));
            int after = m.Index + m.Length;

            if (char.ToLowerInvariant(text[m.Index + 1]) == 'd')   // #define
            {
                var d = DefineTail.Match(text, after);
                if (d.Success && d.Index == after)
                {
                    defines[d.Groups[1].Value] = Substitute(d.Groups[2].Value, defines);
                    pos = after + d.Length;
                }
                else pos = after;   // malformed — drop just the directive token and keep scanning
            }
            else   // #include
            {
                var inc = IncludeTail.Match(text, after);
                if (inc.Success && inc.Index == after)
                {
                    if (readInclude != null && depth < 16)
                    {
                        string? incText = readInclude(Substitute(inc.Groups[1].Value, defines));
                        if (incText != null)
                            sb.Append('\n').Append(Preprocess(incText, readInclude, defines, depth + 1)).Append('\n');
                    }
                    pos = after + inc.Length;
                }
                else pos = after;
            }
        }
        return sb.ToString();
    }

    /// <summary>Removes <c>/* block */</c> and <c>// line</c> comments in one pass.</summary>
    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                int nl = text.IndexOf('\n', i + 2);
                if (nl < 0) { i = text.Length; }
                else { sb.Append('\n'); i = nl + 1; }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    private static string Substitute(string line, Dictionary<string, string> defines)
    {
        if (defines.Count == 0 || line.IndexOf('$') < 0) return line;
        return Variable.Replace(line, m => defines.TryGetValue(m.Value, out var v) ? v : m.Value);
    }

    // ── Tokenize + cascade ──────────────────────────────────────────────

    private static SfzInstrument BuildRegions(string text)
    {
        var control = new SfzControl();
        var regions = new List<SfzRegion>();
        var ignoredHeaders = new Dictionary<string, int>(StringComparer.Ordinal);
        var controlIgnored = new Dictionary<string, int>(StringComparer.Ordinal);

        // Cascade scopes. Each new header at a given level resets it and all
        // lower levels (a new <master> clears the running <group>, etc.).
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var master = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var group = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? region = null;

        // default_path is positional, not a one-shot <control> setting: a bank can set it several
        // times (VSCO keyswitch files, the Discord Melodic/ then Drums/ master) and each applies to
        // the regions that follow. Track the current value and stamp it onto each flushed region.
        string defaultPath = string.Empty;

        // Scope of the opcodes currently being collected.
        var scope = Scope.Global;

        void FlushRegion()
        {
            if (region == null) return;
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in global) merged[kv.Key] = kv.Value;
            foreach (var kv in master) merged[kv.Key] = kv.Value;
            foreach (var kv in group) merged[kv.Key] = kv.Value;
            foreach (var kv in region) merged[kv.Key] = kv.Value;
            merged["default_path"] = defaultPath;   // the path in effect at this region's position
            regions.Add(new SfzRegion(merged));
            region = null;
        }

        var matches = Anchor.Matches(text);
        for (int m = 0; m < matches.Count; m++)
        {
            var match = matches[m];
            string token = match.Value;

            if (token[0] == '<')
            {
                FlushRegion();
                string header = token.Substring(1, token.Length - 2).ToLowerInvariant();
                switch (header)
                {
                    case "control": scope = Scope.Control; break;
                    case "global": global.Clear(); master.Clear(); group.Clear(); scope = Scope.Global; break;
                    case "master": master.Clear(); group.Clear(); scope = Scope.Master; break;
                    case "group": group.Clear(); scope = Scope.Group; break;
                    case "region": region = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); scope = Scope.Region; break;
                    default:  // <curve>, <effect>, <sample>, … — skipped, but recorded for diagnostics
                        ignoredHeaders.TryGetValue(header, out int count);
                        ignoredHeaders[header] = count + 1;
                        scope = Scope.Ignore;
                        break;
                }
                continue;
            }

            // opcode= : value is text up to the next anchor (or end).
            string key = token.Substring(0, token.Length - 1).ToLowerInvariant();
            int valueStart = match.Index + match.Length;
            int valueEnd = (m + 1 < matches.Count) ? matches[m + 1].Index : text.Length;
            string value = text.Substring(valueStart, valueEnd - valueStart).Trim();

            // default_path is positional in any scope — capture it and stamp it per region (above),
            // rather than letting it cascade or be a single global <control> value.
            if (key == "default_path")
            {
                defaultPath = value.Replace('\\', '/');
                continue;
            }

            switch (scope)
            {
                case Scope.Control:
                    if (!ApplyControl(control, key, value))
                    {
                        controlIgnored.TryGetValue(key, out int cc);
                        controlIgnored[key] = cc + 1;
                    }
                    break;
                case Scope.Global: global[key] = value; break;
                case Scope.Master: master[key] = value; break;
                case Scope.Group: group[key] = value; break;
                case Scope.Region: region![key] = value; break;
                case Scope.Ignore: break;
            }
        }

        FlushRegion();
        return new SfzInstrument
        {
            Control = control,
            Regions = regions,
            IgnoredHeaders = ignoredHeaders,
            ControlIgnored = controlIgnored,
        };
    }

    /// <summary>Apply a <c>&lt;control&gt;</c> opcode. Returns false if it's not one we act on.</summary>
    private static bool ApplyControl(SfzControl control, string key, string value)
    {
        // set_ccN (0..127) / set_hdccN (0..1 → 0..127): initial value for controller N.
        if (key.StartsWith("set_hdcc", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hcc) &&
            hcc is >= 0 and <= 127)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double hv))
                control.InitialControllers[hcc] = Math.Clamp((int)Math.Round(hv * 127.0), 0, 127);
            return true;
        }
        if (key.StartsWith("set_cc", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cc) &&
            cc is >= 0 and <= 127)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                control.InitialControllers[cc] = Math.Clamp(v, 0, 127);
            return true;
        }

        switch (key)
        {
            // default_path is handled positionally in BuildRegions (it can change mid-file), not here.
            case "octave_offset":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int oo))
                    control.OctaveOffset = oo;
                return true;
            case "note_offset":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int no))
                    control.NoteOffset = no;
                return true;
            default:
                return false;
        }
    }

    private enum Scope { Control, Global, Master, Group, Region, Ignore }
}
