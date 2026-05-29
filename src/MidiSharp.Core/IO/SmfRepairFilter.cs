using System;
using System.Collections.Generic;

namespace MidiSharp.IO;

/// <summary>The kind of defect a <see cref="SmfRepairFilter"/> found.</summary>
public enum SmfDefectKind
{
    /// <summary>No <c>MThd</c> header — the input is not a Standard MIDI File.</summary>
    NotAMidiFile,
    /// <summary>The <c>MThd</c> header is structurally invalid.</summary>
    HeaderAnomaly,
    /// <summary>A track's declared chunk length did not match its real extent.</summary>
    ChunkLengthCorrupted,
    /// <summary>A meta event's declared length over-ran its data (swallowing following events).</summary>
    MetaLengthOvershoot,
    /// <summary>A track's leading delta-time byte was dropped, so its data starts on a status/meta byte.</summary>
    DroppedLeadingDelta,
    /// <summary>An event could not be repaired; the track was truncated at that point.</summary>
    MalformedEvent,
    /// <summary>A track carried no End-of-Track meta event.</summary>
    MissingEndOfTrack,
    /// <summary>Bytes outside any track chunk (junk between chunks or after the last track).</summary>
    TrailingBytes,
    /// <summary>The header's track count did not match the number of chunks present.</summary>
    TrackCountMismatch,
    /// <summary>Newline-translation signature: musical bytes that may have been 0x0A, now ambiguous.</summary>
    AmbiguousMusicalBytes,
}

/// <summary>What the filter did about a <see cref="SmfDefect"/>.</summary>
public enum SmfDefectAction
{
    /// <summary>The byte stream was corrected; the output parses cleanly here.</summary>
    Corrected,
    /// <summary>The defect could not be repaired from the file alone.</summary>
    Uncorrectable,
    /// <summary>Noted for the record; no correction was attempted.</summary>
    Flagged,
}

/// <summary>One defect located by <see cref="SmfRepairFilter.Scan"/>.</summary>
public sealed record SmfDefect(
    int Offset,
    int? Track,
    SmfDefectKind Kind,
    SmfDefectAction Action,
    string Description)
{
    public override string ToString() => $"[{Action}] 0x{Offset:X4} {Kind}: {Description}";
}

/// <summary>
/// The result of scanning a MIDI byte stream: the (possibly repaired) data plus
/// an itemised list of every defect found and what was done about it.
/// </summary>
public sealed record SmfRepairResult(byte[] Data, bool Modified, IReadOnlyList<SmfDefect> Defects)
{
    public bool HasDefects => Defects.Count > 0;

    /// <summary>Count of defects that were corrected in the output.</summary>
    public int CorrectedCount
    {
        get
        {
            var n = 0;
            foreach (var d in Defects)
                if (d.Action == SmfDefectAction.Corrected) n++;
            return n;
        }
    }
}

/// <summary>
/// A pre-filter that scans a raw Standard MIDI File byte stream, corrects the
/// recoverable structural defects common in old/transferred files, and reports
/// exactly what it changed. The repaired byte[] is meant to be handed straight
/// to <see cref="MidiFileReader"/>, which can then stay a strict parser.
/// </summary>
/// <remarks>
/// Corrects three defect classes, each <b>self-validating</b> (a fix is only kept
/// when it makes the chunk parse cleanly to End-of-Track):
/// <list type="bullet">
/// <item><b>ChunkLengthCorrupted</b> — a track-chunk length that disagrees with the
/// track's real extent (e.g. an ASCII-mode transfer flipped a length byte
/// <c>0x0A→0x0D</c>, inflating it by 768). The true length is taken from where the
/// track's End-of-Track actually falls.</item>
/// <item><b>MetaLengthOvershoot</b> — a meta event whose length byte was flipped
/// <c>0x0A→0x0D</c>, swallowing the following events. Restored to <c>0x0A</c>.</item>
/// <item><b>DroppedLeadingDelta</b> — a track missing the leading <c>0x00</c>
/// delta-time before its first event. The byte is re-inserted.</item>
/// </list>
/// What it cannot recover — a <c>0x0D</c> that may once have been <c>0x0A</c> in a
/// note/velocity/delta byte — it only counts and flags, because there is no way to
/// tell a genuine <c>0x0D</c> from a converted one without an external clean copy.
/// On well-formed input it is a no-op: the output is byte-identical and the defect
/// list is empty.
/// </remarks>
public static class SmfRepairFilter
{
    private enum ParseStop { EndOfTrack, EndOfData, DataNoStatus, Overrun, UnsupportedSystem }

    /// <summary>Scan and repair a MIDI byte stream.</summary>
    public static SmfRepairResult Scan(ReadOnlySpan<byte> raw)
    {
        var src = raw.ToArray();
        var defects = new List<SmfDefect>();

        // ── Header ──────────────────────────────────────────────────────
        if (src.Length < 14 || src[0] != 0x4D || src[1] != 0x54 || src[2] != 0x68 || src[3] != 0x64) // "MThd"
        {
            defects.Add(new SmfDefect(0, null, SmfDefectKind.NotAMidiFile, SmfDefectAction.Uncorrectable,
                "no MThd header; not a Standard MIDI File"));
            return new SmfRepairResult(src, false, defects);
        }

        var headerLen = (int)U32(src, 4);
        var headerEnd = 8 + headerLen;
        if (headerLen < 6 || headerEnd > src.Length)
        {
            defects.Add(new SmfDefect(4, null, SmfDefectKind.HeaderAnomaly, SmfDefectAction.Uncorrectable,
                $"MThd length {headerLen} is invalid"));
            return new SmfRepairResult(src, false, defects);
        }

        var declaredTracks = U16(src, 10);

        var output = new List<byte>(src.Length);
        for (var i = 0; i < headerEnd; i++) output.Add(src[i]); // header copied verbatim

        long totalMusical0D = 0;
        var pos = headerEnd;
        var written = 0;

        // ── Tracks ──────────────────────────────────────────────────────
        for (var t = 0; t < declaredTracks; t++)
        {
            var marker = LocateMtrk(src, pos);
            if (marker < 0) break;
            if (marker > pos)
                defects.Add(new SmfDefect(pos, t, SmfDefectKind.TrailingBytes, SmfDefectAction.Corrected,
                    $"{marker - pos} junk byte(s) before track {t}; skipped"));
            if (marker + 8 > src.Length) break;

            var declaredLen = (int)U32(src, marker + 4);
            var dataStart = marker + 8;
            var maxEnd = (int)Math.Min((long)dataStart + declaredLen, src.Length);
            if (maxEnd <= dataStart) maxEnd = src.Length;

            var (data, rawConsumed, eot, musical0D) = RepairTrack(src, dataStart, maxEnd, t, defects);
            totalMusical0D += musical0D;

            // Distinguish a wrong length field from legitimate post-EndOfTrack
            // padding: if the real next chunk sits at the declared end (not right
            // after the EoT), the length was fine and the gap is intentional.
            if (rawConsumed < declaredLen && eot)
            {
                var afterEot = dataStart + rawConsumed;
                var declaredEndPos = dataStart + declaredLen;
                var paddingNotChunk = !IsMtrkAt(src, afterEot)
                    && (declaredEndPos == src.Length || IsMtrkAt(src, declaredEndPos));
                if (paddingNotChunk)
                {
                    for (var i = afterEot; i < declaredEndPos && i < src.Length; i++) data.Add(src[i]);
                    rawConsumed = declaredLen;
                }
            }

            if (declaredLen != data.Count)
            {
                var note = declaredLen - data.Count == 768
                    ? " (0x0A→0x0D in length byte[2]; newline-translation)"
                    : "";
                defects.Add(new SmfDefect(marker + 4, t, SmfDefectKind.ChunkLengthCorrupted, SmfDefectAction.Corrected,
                    $"track {t}: declared length {declaredLen} corrected to {data.Count}{note}"));
            }

            if (!eot)
                defects.Add(new SmfDefect(dataStart, t, SmfDefectKind.MissingEndOfTrack, SmfDefectAction.Flagged,
                    $"track {t}: no End-of-Track meta event"));

            output.Add(0x4D); output.Add(0x54); output.Add(0x72); output.Add(0x6B); // "MTrk"
            WriteU32(output, (uint)data.Count);
            output.AddRange(data);

            pos = dataStart + rawConsumed;
            written++;
        }

        if (written != declaredTracks)
        {
            defects.Add(new SmfDefect(10, null, SmfDefectKind.TrackCountMismatch, SmfDefectAction.Corrected,
                $"header declares {declaredTracks} track(s); {written} present — header count rewritten"));
            output[10] = (byte)(written >> 8);
            output[11] = (byte)(written & 0xFF);
        }

        if (pos < src.Length)
            defects.Add(new SmfDefect(pos, null, SmfDefectKind.TrailingBytes, SmfDefectAction.Corrected,
                $"{src.Length - pos} trailing byte(s) after last track; dropped"));

        // Newline-translation signature → flag the unrecoverable musical residue.
        if (CountByte(src, 0x0A) == 0 && totalMusical0D > 0)
            defects.Add(new SmfDefect(0, null, SmfDefectKind.AmbiguousMusicalBytes, SmfDefectAction.Flagged,
                $"newline-translation signature (no 0x0A bytes); {totalMusical0D} ambiguous musical byte(s) " +
                "(a 0x0D that may have been 0x0A) — unrecoverable from this file alone"));

        var outArray = output.ToArray();
        var modified = !SequenceEqual(outArray, src);
        return new SmfRepairResult(outArray, modified, defects);
    }

    /// <summary>
    /// Repair and parse one track's events. Returns the corrected track data (up
    /// to and including End-of-Track), how many <i>source</i> bytes it occupied
    /// (for chaining to the next chunk), whether End-of-Track was reached, and the
    /// count of 0x0D bytes in musical (delta/data) positions.
    /// </summary>
    private static (List<byte> Data, int RawConsumed, bool Eot, int Musical0D) RepairTrack(
        byte[] src, int dataStart, int maxEnd, int trackIndex, List<SmfDefect> defects)
    {
        var work = new byte[maxEnd - dataStart];
        Array.Copy(src, dataStart, work, 0, work.Length);

        var inserted = 0;
        var insertTried = false;
        ParseStop stop;
        int endOff;

        while (true)
        {
            (stop, endOff, _) = ScanEvents(work, work.Length);
            if (stop is ParseStop.EndOfTrack or ParseStop.EndOfData) break;

            var progressed = false;

            if (stop == ParseStop.DataNoStatus)
            {
                // (a) Dropped leading delta: data begins on a status/meta byte.
                if (!insertTried && work.Length > 0 && (work[0] & 0x80) != 0)
                {
                    insertTried = true;
                    var w2 = new byte[work.Length + 1];
                    Array.Copy(work, 0, w2, 1, work.Length); // w2[0] == 0x00
                    var (s2, e2, _) = ScanEvents(w2, w2.Length);
                    if (s2 == ParseStop.EndOfTrack || e2 > endOff)
                    {
                        defects.Add(new SmfDefect(dataStart, trackIndex, SmfDefectKind.DroppedLeadingDelta,
                            SmfDefectAction.Corrected,
                            $"track {trackIndex}: missing leading delta-time before first event; inserted 0x00"));
                        work = w2;
                        inserted++;
                        progressed = true;
                    }
                }

                // (b) Meta-length overshoot: a meta length byte flipped 0x0A→0x0D.
                if (!progressed)
                {
                    for (var c = Math.Min(endOff, work.Length - 3); c >= 0; c--)
                    {
                        if (work[c] != 0xFF || work[c + 1] >= 0x80 || work[c + 2] != 0x0D) continue;
                        work[c + 2] = 0x0A;
                        var (s3, e3, _) = ScanEvents(work, work.Length);
                        if (s3 == ParseStop.EndOfTrack || e3 > endOff)
                        {
                            defects.Add(new SmfDefect(dataStart + c + 2, trackIndex, SmfDefectKind.MetaLengthOvershoot,
                                SmfDefectAction.Corrected,
                                $"track {trackIndex}: meta length 0x0D→0x0A at +{c + 2} (newline-translation)"));
                            progressed = true;
                            break;
                        }
                        work[c + 2] = 0x0D; // didn't help — revert and keep looking
                    }
                }
            }

            if (!progressed)
            {
                defects.Add(new SmfDefect(dataStart + endOff, trackIndex, SmfDefectKind.MalformedEvent,
                    SmfDefectAction.Uncorrectable,
                    $"track {trackIndex}: unrepairable {stop} at +{endOff}; track truncated here"));
                break;
            }
        }

        var trueLen = endOff;
        var (_, _, musical) = ScanEvents(work, trueLen);

        var data = new List<byte>(trueLen);
        for (var i = 0; i < trueLen; i++) data.Add(work[i]);

        return (data, trueLen - inserted, stop == ParseStop.EndOfTrack, musical);
    }

    /// <summary>
    /// Walk a track's events from offset 0 to <paramref name="end"/>. Returns why
    /// it stopped, the offset where it stopped (after End-of-Track, or where it
    /// failed), and a count of 0x0D bytes in delta-time / channel-data positions.
    /// </summary>
    private static (ParseStop Stop, int EndOffset, int Musical0D) ScanEvents(byte[] buf, int end)
    {
        var q = 0;
        byte rs = 0;
        var mus = 0;

        while (q < end)
        {
            var eventStart = q;

            // Delta-time (a VLQ; its bytes are musical timing).
            if (!TryReadVlq(buf, ref q, end, countMusical: true, ref mus, out _))
                return (ParseStop.Overrun, eventStart, mus);
            if (q >= end)
                return (ParseStop.EndOfData, q, mus); // trailing delta, no event

            var st = buf[q];
            if ((st & 0x80) != 0)
            {
                q++;
                if (st == 0xFF) // meta
                {
                    if (q >= end) return (ParseStop.Overrun, eventStart, mus);
                    var type = buf[q]; q++;
                    if (!TryReadVlq(buf, ref q, end, countMusical: false, ref mus, out var mlen))
                        return (ParseStop.Overrun, eventStart, mus);
                    if (q + mlen > end) return (ParseStop.Overrun, eventStart, mus);
                    q += mlen;
                    if (type == 0x2F) return (ParseStop.EndOfTrack, q, mus);
                    continue;
                }
                if (st is 0xF0 or 0xF7) // sysex
                {
                    if (!TryReadVlq(buf, ref q, end, countMusical: false, ref mus, out var slen))
                        return (ParseStop.Overrun, eventStart, mus);
                    if (q + slen > end) return (ParseStop.Overrun, eventStart, mus);
                    q += slen;
                    continue;
                }
                if (st >= 0xF0) // other system messages aren't valid in an SMF track
                    return (ParseStop.UnsupportedSystem, q - 1, mus);
                rs = st; // channel status — set running status
            }
            else
            {
                if (rs == 0) return (ParseStop.DataNoStatus, q, mus);
                st = rs;
            }

            var nb = (st & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
            if (q + nb > end) return (ParseStop.Overrun, eventStart, mus);
            for (var j = q; j < q + nb; j++)
                if (buf[j] == 0x0D) mus++;
            q += nb;
        }

        return (ParseStop.EndOfData, end, mus);
    }

    private static bool TryReadVlq(byte[] buf, ref int q, int end, bool countMusical, ref int mus, out int value)
    {
        value = 0;
        while (true)
        {
            if (q >= end) return false;
            var b = buf[q];
            if (countMusical && b == 0x0D) mus++;
            q++;
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return true;
    }

    private static int LocateMtrk(byte[] d, int pos)
    {
        if (IsMtrkAt(d, pos)) return pos;
        for (var i = pos; i + 4 <= d.Length; i++)
            if (d[i] == 0x4D && d[i + 1] == 0x54 && d[i + 2] == 0x72 && d[i + 3] == 0x6B)
                return i;
        return -1;
    }

    private static bool IsMtrkAt(byte[] d, int pos) =>
        pos >= 0 && pos + 4 <= d.Length
        && d[pos] == 0x4D && d[pos + 1] == 0x54 && d[pos + 2] == 0x72 && d[pos + 3] == 0x6B;

    private static uint U32(byte[] d, int o) =>
        ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

    private static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);

    private static void WriteU32(List<byte> o, uint v)
    {
        o.Add((byte)(v >> 24));
        o.Add((byte)(v >> 16));
        o.Add((byte)(v >> 8));
        o.Add((byte)v);
    }

    private static int CountByte(byte[] d, byte b)
    {
        var n = 0;
        foreach (var x in d)
            if (x == b) n++;
        return n;
    }

    private static bool SequenceEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
