using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MidiSharp.SoundBank.Sfz;

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

    // #include can appear anywhere — real banks (e.g. Discord GM) tack it onto
    // the end of a <master> line so the master's opcodes cascade into the
    // included regions.
    private static readonly Regex Include =
        new(@"#include\s+""([^""]+)""", RegexOptions.Compiled);

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

        // Collect/strip line-leading #define and substitute $vars.
        var sb = new StringBuilder(text.Length);
        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("#define", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Substring("#define".Length).Trim();
                int sp = IndexOfWhitespace(parts);
                if (sp > 0)
                {
                    string vname = parts.Substring(0, sp);
                    string val = parts.Substring(sp).Trim();
                    if (vname.StartsWith("$", StringComparison.Ordinal))
                        defines[vname] = val;
                }
                continue;
            }

            sb.Append(Substitute(line, defines));
            sb.Append('\n');
        }

        // Inline #include directives wherever they appear (a newline is inserted
        // before the included content so it can't fuse with a preceding header).
        string withDefines = sb.ToString();
        if (readInclude == null || depth >= 16 || withDefines.IndexOf("#include", StringComparison.OrdinalIgnoreCase) < 0)
            return withDefines;

        return Include.Replace(withDefines, m =>
        {
            string? incText = readInclude(Substitute(m.Groups[1].Value, defines));
            return incText != null ? "\n" + Preprocess(incText, readInclude, defines, depth + 1) + "\n" : string.Empty;
        });
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

        // Cascade scopes. Each new header at a given level resets it and all
        // lower levels (a new <master> clears the running <group>, etc.).
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var master = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var group = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? region = null;

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
                    default: scope = Scope.Ignore; break;  // <curve>, <effect>, <sample>, …
                }
                continue;
            }

            // opcode= : value is text up to the next anchor (or end).
            string key = token.Substring(0, token.Length - 1).ToLowerInvariant();
            int valueStart = match.Index + match.Length;
            int valueEnd = (m + 1 < matches.Count) ? matches[m + 1].Index : text.Length;
            string value = text.Substring(valueStart, valueEnd - valueStart).Trim();

            switch (scope)
            {
                case Scope.Control: ApplyControl(control, key, value); break;
                case Scope.Global: global[key] = value; break;
                case Scope.Master: master[key] = value; break;
                case Scope.Group: group[key] = value; break;
                case Scope.Region: region![key] = value; break;
                case Scope.Ignore: break;
            }
        }

        FlushRegion();
        return new SfzInstrument { Control = control, Regions = regions };
    }

    private static void ApplyControl(SfzControl control, string key, string value)
    {
        switch (key)
        {
            case "default_path":
                control.DefaultPath = value.Replace('\\', '/');
                break;
            case "octave_offset":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int oo))
                    control.OctaveOffset = oo;
                break;
            case "note_offset":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int no))
                    control.NoteOffset = no;
                break;
        }
    }

    // ── Small helpers ───────────────────────────────────────────────────

    private static int IndexOfWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (char.IsWhiteSpace(s[i])) return i;
        return -1;
    }

    private enum Scope { Control, Global, Master, Group, Region, Ignore }
}
