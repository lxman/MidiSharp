using System.Text;
using MidiSharp.Lyrics;
using MidiSharp.Model.Events;
using Xunit;

namespace MidiSharp.Core.Tests.Lyrics;

public class LyricStreamTests
{
    private static MetaEvent Make(string s) =>
        new() { Type = MetaEventType.Lyric, Data = Encoding.ASCII.GetBytes(s) };

    private static MetaEvent MakeRaw(byte[] bytes) =>
        new() { Type = MetaEventType.Lyric, Data = bytes };

    [Fact]
    public void PlainText_DecodesAsAscii()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(Make("Hello "));
        Assert.Equal("Hello ", seg.Text);
        Assert.Equal(LyricFlags.None, seg.Flags);
        Assert.Null(seg.RubyText);
    }

    [Fact]
    public void EmptyEvent_IsMelisma()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(Make(""));
        Assert.Equal(string.Empty, seg.Text);
        Assert.True(seg.Flags.HasFlag(LyricFlags.Melisma));
    }

    [Fact]
    public void RawCr_FlagsEndOfLine()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(MakeRaw([0x0D]));
        Assert.True(seg.Flags.HasFlag(LyricFlags.EndOfLine));
        Assert.Equal(string.Empty, seg.Text);
    }

    [Fact]
    public void RawLf_FlagsEndOfParagraph()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(MakeRaw([0x0A]));
        Assert.True(seg.Flags.HasFlag(LyricFlags.EndOfParagraph));
    }

    [Fact]
    public void BackslashR_FlagsEndOfLine()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(Make(@"\r"));
        Assert.True(seg.Flags.HasFlag(LyricFlags.EndOfLine));
        Assert.Equal(string.Empty, seg.Text);
    }

    [Fact]
    public void BackslashN_FlagsEndOfParagraph()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(Make(@"\n"));
        Assert.True(seg.Flags.HasFlag(LyricFlags.EndOfParagraph));
    }

    [Fact]
    public void BackslashEscapes_RenderLiteralCharacters()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(Make(@"a\{b\}c\[d\]e\\f"));
        Assert.Equal("a{b}c[d]e\\f", seg.Text);
    }

    [Fact]
    public void RubyBracket_ExtractsRubyText()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(Make("kanji[reading]"));
        Assert.Equal("kanji", seg.Text);
        Assert.Equal("reading", seg.RubyText);
    }

    [Fact]
    public void LanguageTag_SwitchesEncoding()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(Make("{@JP}"));
        Assert.Equal("JP", ls.LanguageCode);
        Assert.True(seg.Flags.HasFlag(LyricFlags.LanguageChanged));
        Assert.Equal(string.Empty, seg.Text);
    }

    [Fact]
    public void JapaneseShiftJis_DecodesAfterLanguageSwitch()
    {
        var ls = new LyricStream();
        ls.Parse(Make("{@JP}"));
        // Shift-JIS bytes for "美" (kanji "beautiful") = 0x94 0xFC
        LyricSegment seg = ls.Parse(MakeRaw([0x94, 0xFC]));
        Assert.Equal("美", seg.Text);
    }

    [Fact]
    public void SongInfoTitle_UpdatesSongInfo()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(Make("{#Title=Hello World}"));
        Assert.True(seg.Flags.HasFlag(LyricFlags.SongInfoChanged));
        Assert.Equal("Hello World", ls.SongInfo.Title);
    }

    [Fact]
    public void SongInfoAllFields_Populated()
    {
        var ls = new LyricStream();
        ls.Parse(Make("{#Title=Song}"));
        ls.Parse(Make("{#Composer=Bach}"));
        ls.Parse(Make("{#Lyrics=Mahler}"));
        ls.Parse(Make("{#Artist=Choir}"));
        Assert.Equal("Song", ls.SongInfo.Title);
        Assert.Equal("Bach", ls.SongInfo.Composer);
        Assert.Equal("Mahler", ls.SongInfo.Lyricist);
        Assert.Equal("Choir", ls.SongInfo.Artist);
    }

    [Fact]
    public void NullItemTag_ConsumedButDoesNotChangeSongInfo()
    {
        // Per RP-026 §5, {#} terminates the song-info group but doesn't update any field.
        var ls = new LyricStream();
        ls.Parse(Make("{#Title=Test}"));
        LyricSegment seg = ls.Parse(Make("{#}"));
        Assert.Equal(string.Empty, seg.Text);
        Assert.Equal("Test", ls.SongInfo.Title);  // prior title still set
    }

    [Fact]
    public void UnicodeBom_LittleEndian_SwitchesToUtf16()
    {
        var ls = new LyricStream();
        var bytes = new byte[] { 0xFF, 0xFE, 0x48, 0x00, 0x69, 0x00 }; // BOM + "Hi"
        LyricSegment seg = ls.Parse(MakeRaw(bytes));
        Assert.Equal("Hi", seg.Text);
        Assert.True(seg.Flags.HasFlag(LyricFlags.LanguageChanged));
    }

    [Fact]
    public void Rp017Example_ParsesAsSyllables()
    {
        var ls = new LyricStream();
        // From RP-017 example: "syl" "la" "ble " all parse as plain text.
        Assert.Equal("syl", ls.Parse(Make("syl")).Text);
        Assert.Equal("la", ls.Parse(Make("la")).Text);
        LyricSegment s = ls.Parse(Make("ble "));
        Assert.Equal("ble ", s.Text);
        Assert.Equal(LyricFlags.None, s.Flags);
    }

    [Fact]
    public void MixedTextAndControls_PreservesOrder()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(Make("end of line.\\r"));
        Assert.Equal("end of line.", seg.Text);
        Assert.True(seg.Flags.HasFlag(LyricFlags.EndOfLine));
    }

    [Fact]
    public void UnterminatedTag_FallsBackGracefully()
    {
        var ls = new LyricStream();
        LyricSegment seg = ls.Parse(Make("{#Title=Unterminated"));
        // Unterminated {#...} reads to end of event, still extracts title.
        Assert.Equal("Unterminated", ls.SongInfo.Title);
    }
}
