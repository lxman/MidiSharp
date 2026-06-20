using System;
using System.Collections.Generic;
using System.Linq;
using MidiSharp.IO;
using MidiSharp.Model.Events;
using Xunit;

namespace MidiSharp.Core.Tests.IO;

public class SmfRepairFilterTests
{
    // ── fixture helpers ───────────────────────────────────────────────
    private static byte[] Concat(params byte[][] parts)
    {
        var list = new List<byte>();
        foreach (var p in parts) list.AddRange(p);
        return list.ToArray();
    }

    private static byte[] Mthd(int format, int ntrk, int division) =>
    [
        0x4D, 0x54, 0x68, 0x64, 0x00, 0x00, 0x00, 0x06,
        (byte)(format >> 8), (byte)format,
        (byte)(ntrk >> 8), (byte)ntrk,
        (byte)(division >> 8), (byte)division,
    ];

    private static byte[] MtrkHeader(int declaredLen) =>
    [
        0x4D, 0x54, 0x72, 0x6B,
        (byte)(declaredLen >> 24), (byte)(declaredLen >> 16),
        (byte)(declaredLen >> 8), (byte)declaredLen,
    ];

    // delta0 note-on C4 v100, delta96 note-off, delta0 EoT  (12 bytes)
    private static readonly byte[] NoteTrack =
        [0x00, 0x90, 0x3C, 0x64, 0x60, 0x80, 0x3C, 0x40, 0x00, 0xFF, 0x2F, 0x00];

    // ── clean input is a no-op ────────────────────────────────────────
    [Fact]
    public void Scan_CleanFile_IsByteIdenticalNoOp()
    {
        var input = Concat(Mthd(0, 1, 96), MtrkHeader(NoteTrack.Length), NoteTrack);

        var result = SmfRepairFilter.Scan(input);

        Assert.False(result.Modified);
        Assert.Empty(result.Defects);
        Assert.Equal(input, result.Data);
        // and it parses
        Assert.Single(MidiFileReader.Read(result.Data).Tracks);
    }

    // ── ChunkLengthCorrupted (the wtcbki +768 family, shrunk to +5) ───
    [Fact]
    public void Scan_InflatedChunkLength_CorrectedToTrueExtent()
    {
        var tempoTrack = new byte[]
        {
            0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20, // set tempo
            0x00, 0xFF, 0x2F, 0x00,                   // EoT  (11 bytes total)
        };
        // Track 0 claims 16 bytes but is really 11 — overruns track 1's marker.
        var input = Concat(
            Mthd(1, 2, 96),
            MtrkHeader(16), tempoTrack,
            MtrkHeader(NoteTrack.Length), NoteTrack);

        var result = SmfRepairFilter.Scan(input);

        Assert.True(result.Modified);
        var d = Assert.Single(result.Defects);
        Assert.Equal(SmfDefectKind.ChunkLengthCorrupted, d.Kind);
        Assert.Equal(SmfDefectAction.Corrected, d.Action);

        var file = MidiFileReader.Read(result.Data);
        Assert.Equal(2, file.Tracks.Count);
        Assert.Contains(file.Tracks[0].Events, e => e is MetaEvent { Type: MetaEventType.SetTempo });
        Assert.Contains(file.Tracks[1].Events, e => e is NoteOnEvent);
    }

    // ── MetaLengthOvershoot (the "RH L /LH H" family) ─────────────────
    [Fact]
    public void Scan_MetaLengthOvershoot_RestoresSwallowedEvents()
    {
        // text meta "ABCDEFGHIJ" (10 chars) but length byte corrupted 0x0A->0x0D,
        // swallowing the 3 bytes that begin the following note-on.
        byte[] Track(byte metaLen) => Concat(
            [0x00, 0xFF, 0x01, metaLen],
            "ABCDEFGHIJ"u8.ToArray(),
            NoteTrack);

        var corrupted = Track(0x0D);
        var input = Concat(Mthd(0, 1, 96), MtrkHeader(corrupted.Length), corrupted);

        // sanity: a strict read of the corrupted bytes throws
        Assert.ThrowsAny<Exception>(() => MidiFileReader.Read(input));

        var result = SmfRepairFilter.Scan(input);

        Assert.True(result.Modified);
        var d = Assert.Single(result.Defects);
        Assert.Equal(SmfDefectKind.MetaLengthOvershoot, d.Kind);
        Assert.Equal(SmfDefectAction.Corrected, d.Action);

        var track = MidiFileReader.Read(result.Data).Tracks.Single();
        Assert.Contains(track.Events, e => e is NoteOnEvent { Note: 0x3C, Velocity: 0x64 });
        Assert.Contains(track.Events, e => e is MetaEvent { Type: MetaEventType.EndOfTrack });
    }

    // ── DroppedLeadingDelta (the airgstr4 family) ─────────────────────
    [Fact]
    public void Scan_DroppedLeadingDelta_ReinsertsZeroByte()
    {
        // intended: 00 FF 04 04 "Bach" 00 FF 2F 00  (12 bytes); the leading 00 is gone.
        var dropped = new byte[]
        {
            0xFF, 0x04, 0x04, 0x42, 0x61, 0x63, 0x68, 0x00, 0xFF, 0x2F, 0x00, // 11 bytes
        };
        // length field still says 12 (correct for the intended content).
        var input = Concat(Mthd(0, 1, 96), MtrkHeader(12), dropped);

        var result = SmfRepairFilter.Scan(input);

        Assert.True(result.Modified);
        var d = Assert.Single(result.Defects);
        Assert.Equal(SmfDefectKind.DroppedLeadingDelta, d.Kind);
        Assert.Equal(SmfDefectAction.Corrected, d.Action);

        var track = MidiFileReader.Read(result.Data).Tracks.Single();
        var name = Assert.IsType<MetaEvent>(track.Events[0]);
        Assert.Equal(MetaEventType.InstrumentName, name.Type);
        Assert.Equal("Bach", name.Text);
    }

    // ── AmbiguousMusicalBytes flag (newline-conversion residue) ───────
    [Fact]
    public void Scan_NewlineConvertedMusicalByte_FlaggedNotChanged()
    {
        // clean-parsing track, but velocity byte is 0x0D and the file has zero 0x0A.
        var track = new byte[]
        {
            0x00, 0x90, 0x3C, 0x0D, 0x60, 0x80, 0x3C, 0x40, 0x00, 0xFF, 0x2F, 0x00,
        };
        var input = Concat(Mthd(0, 1, 96), MtrkHeader(track.Length), track);
        Assert.DoesNotContain((byte)0x0A, input);

        var result = SmfRepairFilter.Scan(input);

        // nothing corrected — bytes are left exactly as they are…
        Assert.False(result.Modified);
        Assert.Equal(input, result.Data);
        Assert.Equal(0, result.CorrectedCount);
        // …but the ambiguity is on the record.
        var d = Assert.Single(result.Defects);
        Assert.Equal(SmfDefectKind.AmbiguousMusicalBytes, d.Kind);
        Assert.Equal(SmfDefectAction.Flagged, d.Action);
    }

    [Fact]
    public void Scan_NotAMidiFile_ReportedAndUnchanged()
    {
        var input = "not a midi file at all"u8.ToArray();

        var result = SmfRepairFilter.Scan(input);

        Assert.False(result.Modified);
        var d = Assert.Single(result.Defects);
        Assert.Equal(SmfDefectKind.NotAMidiFile, d.Kind);
    }
}
