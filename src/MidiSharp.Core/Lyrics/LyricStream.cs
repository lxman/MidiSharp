using System;
using System.Text;
using MidiSharp.Model.Events;

namespace MidiSharp.Lyrics;

/// <summary>
/// Stateful parser for SMF Lyric/Display Meta Events per RP-026 (SMF Language and
/// Display Extensions, MMA/AMEI 1999). Pass each Lyric or Text meta event through
/// <see cref="Parse(MetaEvent)"/> in order — the parser carries persistent state
/// (active encoding, accumulated song info) across calls so multi-event sequences
/// like song-info headers and language switches work correctly.
///
/// <para>Use this when building a Karaoke / lyric display UI on top of MidiSharp.
/// The synth doesn't need it — it just dispatches the raw bytes via
/// <c>RealtimePlayer.MetaEventDispatched</c>.</para>
///
/// <para>Handles:
/// <list type="bullet">
///   <item>Backslash command codes: <c>\r</c> CR, <c>\n</c> LF, <c>\t</c> Tab, plus
///   escapes for the literal characters <c>\\ \{ \} \[ \]</c>.</item>
///   <item>Language tags <c>{@LATIN}</c> / <c>{@JP}</c> switching ANSI / Shift-JIS.</item>
///   <item>Song info tags <c>{#Title=...}</c>, <c>{#Composer=...}</c>,
///   <c>{#Lyrics=...}</c>, <c>{#Artist=...}</c>, plus the <c>{#}</c> null terminator.</item>
///   <item>Ruby brackets <c>[ruby]</c> for Japanese pronunciation annotations.</item>
///   <item>UNICODE BOM (0xFF 0xFE or 0xFE 0xFF) at start of an event switches to UTF-16.</item>
///   <item>Raw CR / LF / Tab bytes (RP-017 legacy) as fallback.</item>
/// </list>
/// </para>
/// </summary>
public sealed class LyricStream
{
    private Encoding _encoding = Encoding.ASCII;
    private bool _unicodeBomSeen;
    private static bool s_codePagesRegistered;

    /// <summary>Accumulated song metadata parsed from <c>{#...=...}</c> tags.</summary>
    public LyricSongInfo SongInfo { get; } = new();

    /// <summary>Most recently selected language code ("LATIN", "JP", or whatever was
    /// in the most recent <c>{@...}</c> tag). Defaults to "LATIN".</summary>
    public string LanguageCode { get; private set; } = "LATIN";

    /// <summary>Creates a new parser. State (encoding + song info) accumulates across
    /// calls to <see cref="Parse(MetaEvent)"/> — instantiate one per MIDI file / playback.</summary>
    public LyricStream()
    {
        // Register the legacy code pages provider once per AppDomain. Required for
        // Shift-JIS (code page 932) and Windows-1252 to work via Encoding.GetEncoding.
        if (!s_codePagesRegistered)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            s_codePagesRegistered = true;
        }
    }

    /// <summary>Parse a lyric/text meta event. The returned segment carries any
    /// visible text and control flags. Side effects: updates <see cref="SongInfo"/>,
    /// <see cref="LanguageCode"/>, and internal encoding state.</summary>
    public LyricSegment Parse(MetaEvent evt)
    {
        if (evt == null) throw new ArgumentNullException(nameof(evt));
        return Parse(evt.Data.Span);
    }

    /// <inheritdoc cref="Parse(MetaEvent)"/>
    public LyricSegment Parse(ReadOnlySpan<byte> data)
    {
        var flags = LyricFlags.None;

        // BOM detection at start of an event switches us to UTF-16 for the lifetime
        // of the parser. The spec allows BOMs to appear anywhere, but in practice
        // they only show up at the start of the first lyric event.
        if (!_unicodeBomSeen && data.Length >= 2)
        {
            if (data[0] == 0xFF && data[1] == 0xFE)
            {
                _encoding = Encoding.Unicode;       // little-endian UTF-16
                _unicodeBomSeen = true;
                data = data[2..];
                flags |= LyricFlags.LanguageChanged;
            }
            else if (data[0] == 0xFE && data[1] == 0xFF)
            {
                _encoding = Encoding.BigEndianUnicode;
                _unicodeBomSeen = true;
                data = data[2..];
                flags |= LyricFlags.LanguageChanged;
            }
        }

        if (data.IsEmpty)
        {
            // Per RP-017 §7, an empty Lyric event signals a melisma — the previous
            // syllable continues being sung. No text, no break.
            return new LyricSegment(string.Empty, null, flags | LyricFlags.Melisma);
        }

        var raw = _encoding.GetString(data);
        var sb = new StringBuilder(raw.Length);
        string? ruby = null;
        var i = 0;

        while (i < raw.Length)
        {
            var c = raw[i];

            // Backslash command codes (RP-026 §3).
            if (c == '\\' && i + 1 < raw.Length)
            {
                var next = raw[i + 1];
                switch (next)
                {
                    case 'r': flags |= LyricFlags.EndOfLine; i += 2; continue;
                    case 'n': flags |= LyricFlags.EndOfParagraph; i += 2; continue;
                    case 't': flags |= LyricFlags.Tab; sb.Append('\t'); i += 2; continue;
                    case '\\': case '{': case '}': case '[': case ']':
                        sb.Append(next); i += 2; continue;
                }
                // Unknown backslash escape — emit the backslash literally and continue.
            }

            // Tag prefix: {@code} = language, {#key=value} = song info.
            if (c == '{' && i + 1 < raw.Length)
            {
                var tagType = raw[i + 1];
                if (tagType == '@')
                {
                    var end = raw.IndexOf('}', i + 2);
                    if (end > 0)
                    {
                        var code = raw.Substring(i + 2, end - (i + 2)).Trim();
                        SwitchLanguage(code);
                        flags |= LyricFlags.LanguageChanged;
                        i = end + 1;
                        continue;
                    }
                    // Unterminated tag — bail out, treat rest as text.
                }
                else if (tagType == '#')
                {
                    var end = raw.IndexOf('}', i + 2);
                    if (end < 0) end = raw.Length;
                    var content = raw.Substring(i + 2, end - (i + 2));
                    if (content.Length > 0)
                    {
                        var eq = content.IndexOf('=');
                        if (eq > 0)
                        {
                            var key = content.Substring(0, eq).Trim();
                            var val = content.Substring(eq + 1).Trim();
                            SetSongInfo(key, val);
                            flags |= LyricFlags.SongInfoChanged;
                        }
                        // {#} null terminator and unknown keys are silently consumed.
                    }
                    i = end + 1;
                    continue;
                }
            }

            // Ruby annotation: text between [ and ] is pronunciation hint for whatever
            // text preceded it in the same event (per RP-026 §4 / §6).
            if (c == '[')
            {
                var end = raw.IndexOf(']', i + 1);
                if (end > 0)
                {
                    ruby = raw.Substring(i + 1, end - (i + 1));
                    i = end + 1;
                    continue;
                }
            }

            // Raw control bytes (RP-017 §4-5 — pre-RP-026 convention).
            if (c == '\r') { flags |= LyricFlags.EndOfLine; i++; continue; }
            if (c == '\n') { flags |= LyricFlags.EndOfParagraph; i++; continue; }
            if (c == '\t') { flags |= LyricFlags.Tab; sb.Append('\t'); i++; continue; }

            sb.Append(c);
            i++;
        }

        return new LyricSegment(sb.ToString(), ruby, flags);
    }

    private void SwitchLanguage(string code)
    {
        var upper = code.ToUpperInvariant();
        LanguageCode = upper;
        // Spec recognises LATIN (ANSI / Windows-1252) and JP (Shift-JIS). Other
        // codes are accepted (we update LanguageCode for the caller) but fall back
        // to ASCII for the byte→char step until a known code appears.
        _encoding = upper switch
        {
            "JP" => SafeGetEncoding(932) ?? Encoding.ASCII,
            "LATIN" => SafeGetEncoding(1252) ?? Encoding.ASCII,
            _ => Encoding.ASCII,
        };
    }

    private static Encoding? SafeGetEncoding(int codepage)
    {
        try { return Encoding.GetEncoding(codepage); }
        catch { return null; }
    }

    private void SetSongInfo(string key, string val)
    {
        switch (key.ToUpperInvariant())
        {
            case "TITLE": SongInfo.Title = val; break;
            case "COMPOSER": SongInfo.Composer = val; break;
            case "LYRICS": SongInfo.Lyricist = val; break;
            case "ARTIST": SongInfo.Artist = val; break;
            // Spec leaves room for future keys; ignore unknown ones.
        }
    }
}

/// <summary>One parsed lyric event: the visible text, an optional Ruby annotation,
/// and bit flags describing any control events that fired in this segment.</summary>
public readonly record struct LyricSegment(string Text, string? RubyText, LyricFlags Flags);

/// <summary>Control events that can fire as part of a single Lyric event.</summary>
[Flags]
public enum LyricFlags
{
    /// <summary>Plain text only.</summary>
    None = 0,
    /// <summary>Segment contained a CR (<c>\r</c> or 0x0D) — break the display line.</summary>
    EndOfLine = 1,
    /// <summary>Segment contained an LF (<c>\n</c> or 0x0A) — clear the screen / next page.</summary>
    EndOfParagraph = 2,
    /// <summary>Segment contained a tab (<c>\t</c> or 0x09). The tab itself is included in Text.</summary>
    Tab = 4,
    /// <summary>Empty lyric event — previous syllable continues (melisma, RP-017 §7).</summary>
    Melisma = 8,
    /// <summary>Active encoding / language changed during this segment.</summary>
    LanguageChanged = 16,
    /// <summary>Song info was updated; consult <see cref="LyricStream.SongInfo"/>.</summary>
    SongInfoChanged = 32,
}

/// <summary>Song metadata accumulated from <c>{#Title=...}</c> style tags.</summary>
public sealed class LyricSongInfo
{
    /// <summary>{#Title=...} — song name.</summary>
    public string? Title { get; internal set; }
    /// <summary>{#Composer=...} — composer.</summary>
    public string? Composer { get; internal set; }
    /// <summary>{#Lyrics=...} — lyricist (the spec uses "Lyrics" but it's a person's name).</summary>
    public string? Lyricist { get; internal set; }
    /// <summary>{#Artist=...} — performer.</summary>
    public string? Artist { get; internal set; }
}
