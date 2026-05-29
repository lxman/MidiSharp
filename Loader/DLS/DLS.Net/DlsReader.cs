using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DLS.Net;

/// <summary>
/// Reads a DLS Level 1 or Level 2 file into a <see cref="DlsCollection"/>.
/// </summary>
public static class DlsReader
{
    public static DlsCollection Load(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path required");
        var bytes = File.ReadAllBytes(path);
        return Load(bytes);
    }

    public static DlsCollection Load(byte[] data) => Load(new ReadOnlyMemory<byte>(data));

    public static DlsCollection Load(ReadOnlyMemory<byte> data)
    {
        var rdr = new ChunkReader(data);
        rdr.ExpectRiff("DLS ");

        string? name = null, copyright = null, engineer = null, comments = null;
        var instruments = new List<DlsInstrument>();
        var waves = new List<DlsWave>();

        rdr.ForEachSubChunk((tag, body) =>
        {
            switch (tag)
            {
                case "colh":
                    // Collection header: cInstruments DWORD. We don't actually need
                    // this — the LIST lins iteration tells us how many instruments
                    // are actually present.
                    break;
                case "LIST":
                    var listType = ReadFourCc(body.Span, 0);
                    var listBody = body.Slice(4);
                    switch (listType)
                    {
                        case "lins":
                            ReadInstrumentList(listBody, instruments);
                            break;
                        case "wvpl":
                            ReadWavePool(listBody, waves);
                            break;
                        case "INFO":
                        {
                            var info = ReadInfoChunks(listBody);
                            name = info.Name; copyright = info.Copyright;
                            engineer = info.Engineer; comments = info.Comments;
                            break;
                        }
                    }
                    break;
                case "ptbl":
                    // Pool table — indexed mapping from wave-table-index → cue.
                    // We rely on the natural order of LIST wvpl entries, which
                    // matches every DLS file we've seen in the wild. Skipped.
                    break;
                case "vers":
                case "dlid":
                    // Version, unique ID — informational; skipped.
                    break;
            }
        });

        return new DlsCollection
        {
            Name = name,
            Copyright = copyright,
            Engineer = engineer,
            Comments = comments,
            Instruments = instruments,
            Waves = waves,
        };
    }

    // ── LIST lins ────────────────────────────────────────────────────

    private static void ReadInstrumentList(ReadOnlyMemory<byte> body, List<DlsInstrument> output)
    {
        var rdr = new ChunkReader(body);
        rdr.ForEachSubChunk((tag, b) =>
        {
            if (tag != "LIST") return;
            if (ReadFourCc(b.Span, 0) != "ins ") return;
            output.Add(ReadInstrument(b.Slice(4)));
        });
    }

    private static DlsInstrument ReadInstrument(ReadOnlyMemory<byte> body)
    {
        uint cRegions = 0, locale_bank = 0, locale_prog = 0;
        bool isDrumKit = false;
        string? name = null;
        var regions = new List<DlsRegion>();
        var articulators = new List<ArticulatorList>();

        var rdr = new ChunkReader(body);
        rdr.ForEachSubChunk((tag, b) =>
        {
            var span = b.Span;
            switch (tag)
            {
                case "insh":
                    cRegions = BinaryPrimitives.ReadUInt32LittleEndian(span);
                    locale_bank = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4));
                    locale_prog = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8));
                    // Bit 31 of locale_bank: drum-kit flag.
                    isDrumKit = (locale_bank & 0x80000000u) != 0;
                    break;
                case "LIST":
                    var sub = ReadFourCc(span, 0);
                    var subBody = b.Slice(4);
                    switch (sub)
                    {
                        case "lrgn":
                            ReadRegionList(subBody, regions);
                            break;
                        case "lart":
                            articulators.Add(ReadArticulator(subBody, isLevel2: false));
                            break;
                        case "lar2":
                            articulators.Add(ReadArticulator(subBody, isLevel2: true));
                            break;
                        case "INFO":
                            name = ReadInfoName(subBody);
                            break;
                    }
                    break;
            }
        });

        // Bank in DLS locale: bits 0-14 are MSB+LSB combined (bits 0-6 = LSB, 8-14 = MSB).
        // Most banks use just the LSB; folding to (msb<<7 | lsb) gives a 14-bit bank.
        uint bankLsb = locale_bank & 0x7Fu;
        uint bankMsb = (locale_bank >> 8) & 0x7Fu;
        uint bank = (bankMsb << 7) | bankLsb;
        if (isDrumKit) bank = 128;

        return new DlsInstrument
        {
            Bank = bank,
            Program = locale_prog,
            IsDrumKit = isDrumKit,
            Name = name,
            Regions = regions,
            Articulators = articulators,
        };
    }

    // ── LIST lrgn ────────────────────────────────────────────────────

    private static void ReadRegionList(ReadOnlyMemory<byte> body, List<DlsRegion> output)
    {
        var rdr = new ChunkReader(body);
        rdr.ForEachSubChunk((tag, b) =>
        {
            if (tag != "LIST") return;
            var sub = ReadFourCc(b.Span, 0);
            if (sub != "rgn " && sub != "rgn2") return;
            output.Add(ReadRegion(b.Slice(4)));
        });
    }

    private static DlsRegion ReadRegion(ReadOnlyMemory<byte> body)
    {
        byte keyLow = 0, keyHigh = 127, velLow = 0, velHigh = 127;
        ushort opts = 0, keyGroup = 0;
        WaveLink link = default;
        WaveSampleInfo? wsmp = null;
        var arts = new List<ArticulatorList>();

        var rdr = new ChunkReader(body);
        rdr.ForEachSubChunk((tag, b) =>
        {
            var span = b.Span;
            switch (tag)
            {
                case "rgnh":
                    // RGNHEADER: rangeKey (low,high WORDs), rangeVelocity (low,high WORDs),
                    // fusOptions WORD, usKeyGroup WORD, [rgn2: usLayer WORD].
                    keyLow = (byte)BinaryPrimitives.ReadUInt16LittleEndian(span);
                    keyHigh = (byte)BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2));
                    velLow = (byte)BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
                    velHigh = (byte)BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6));
                    opts = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8));
                    keyGroup = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(10));
                    break;
                case "wsmp":
                    wsmp = ReadWaveSampleInfo(b);
                    break;
                case "wlnk":
                    link = new WaveLink(
                        BinaryPrimitives.ReadUInt16LittleEndian(span),
                        BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2)),
                        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4)),
                        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8)));
                    break;
                case "LIST":
                    var sub = ReadFourCc(span, 0);
                    var subBody = b.Slice(4);
                    if (sub == "lart") arts.Add(ReadArticulator(subBody, isLevel2: false));
                    else if (sub == "lar2") arts.Add(ReadArticulator(subBody, isLevel2: true));
                    break;
            }
        });

        return new DlsRegion
        {
            KeyLow = keyLow,
            KeyHigh = keyHigh,
            VelocityLow = velLow,
            VelocityHigh = velHigh,
            Options = opts,
            KeyGroup = keyGroup,
            WaveLink = link,
            SampleInfo = wsmp,
            Articulators = arts,
        };
    }

    private static WaveSampleInfo ReadWaveSampleInfo(ReadOnlyMemory<byte> body)
    {
        // wsmp: cbSize DWORD, usUnityNote WORD, sFineTune SHORT, lAttenuation/lGain LONG,
        //       fulOptions DWORD, cSampleLoops DWORD, then cSampleLoops × WLOOP records.
        var span = body.Span;
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span);
        ushort unityNote = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
        short fineTune = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(6));
        int gain = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8));
        uint options = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
        uint loopCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));

        var loops = new SampleLoop[loopCount];
        int pos = (int)headerSize;
        for (int i = 0; i < loopCount; i++)
        {
            uint loopHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos));
            uint loopType = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 4));
            uint loopStart = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 8));
            uint loopLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 12));
            loops[i] = new SampleLoop((DlsLoopType)loopType, loopStart, loopLen);
            pos += (int)loopHeaderSize;
        }

        return new WaveSampleInfo
        {
            UnityNote = (byte)unityNote,
            FineTuneCents = fineTune,
            // DLS stores gain in centibels of attenuation (negative = quieter). We
            // keep the sign as-is; the loader negates if needed at translation.
            GainCentibels = -gain / 65536,  // 16.16 fixed-point → integer cB
            Options = options,
            Loops = loops,
        };
    }

    // ── Articulators (lart / lar2) ───────────────────────────────────

    private static ArticulatorList ReadArticulator(ReadOnlyMemory<byte> body, bool isLevel2)
    {
        // lart/lar2 contains one or more art1/art2 chunks. We flatten them.
        var connections = new List<ConnectionBlock>();
        var rdr = new ChunkReader(body);
        rdr.ForEachSubChunk((tag, b) =>
        {
            if (tag != "art1" && tag != "art2") return;
            var span = b.Span;
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span);
            uint connectionCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4));
            int pos = (int)headerSize;
            for (int i = 0; i < connectionCount; i++)
            {
                ushort src = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos));
                ushort ctl = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos + 2));
                ushort dst = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos + 4));
                ushort xform = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos + 6));
                int scale = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 8));
                connections.Add(new ConnectionBlock(
                    (ConnectionSource)src, ctl, (ConnectionDestination)dst, xform, scale));
                pos += 12;
            }
        });
        return new ArticulatorList { IsLevel2 = isLevel2, Connections = connections };
    }

    // ── LIST wvpl ────────────────────────────────────────────────────

    private static void ReadWavePool(ReadOnlyMemory<byte> body, List<DlsWave> output)
    {
        var rdr = new ChunkReader(body);
        int index = 0;
        rdr.ForEachSubChunk((tag, b) =>
        {
            if (tag != "LIST") return;
            if (ReadFourCc(b.Span, 0) != "wave") return;
            output.Add(ReadWave(b.Slice(4), index++));
        });
    }

    private static DlsWave ReadWave(ReadOnlyMemory<byte> body, int index)
    {
        WaveFormatTag formatTag = WaveFormatTag.Pcm;
        ushort channels = 1, bitsPerSample = 16, blockAlign = 2;
        uint sampleRate = 44100;
        string? name = null;
        WaveSampleInfo? wsmp = null;
        ReadOnlyMemory<byte> data = ReadOnlyMemory<byte>.Empty;

        var rdr = new ChunkReader(body);
        rdr.ForEachSubChunk((tag, b) =>
        {
            var span = b.Span;
            switch (tag)
            {
                case "fmt ":
                    formatTag = (WaveFormatTag)BinaryPrimitives.ReadUInt16LittleEndian(span);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2));
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4));
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(12));
                    bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(14));
                    break;
                case "data":
                    data = b;
                    break;
                case "wsmp":
                    wsmp = ReadWaveSampleInfo(b);
                    break;
                case "LIST":
                    if (ReadFourCc(span, 0) == "INFO")
                        name = ReadInfoName(b.Slice(4));
                    break;
            }
        });

        return new DlsWave
        {
            Index = index,
            FormatTag = formatTag,
            Channels = channels,
            SampleRate = sampleRate,
            BitsPerSample = bitsPerSample,
            BlockAlign = blockAlign,
            Name = name,
            SampleInfo = wsmp,
            Data = data,
        };
    }

    // ── INFO chunk (RIFF metadata strings) ──────────────────────────

    private struct InfoStrings { public string? Name, Copyright, Engineer, Comments; }

    private static InfoStrings ReadInfoChunks(ReadOnlyMemory<byte> body)
    {
        var result = new InfoStrings();
        var rdr = new ChunkReader(body);
        rdr.ForEachSubChunk((tag, b) =>
        {
            var text = ReadZeroTerminated(b.Span);
            switch (tag)
            {
                case "INAM": result.Name = text; break;
                case "ICOP": result.Copyright = text; break;
                case "IENG": result.Engineer = text; break;
                case "ICMT": result.Comments = text; break;
            }
        });
        return result;
    }

    private static string? ReadInfoName(ReadOnlyMemory<byte> body)
    {
        string? found = null;
        var rdr = new ChunkReader(body);
        rdr.ForEachSubChunk((tag, b) =>
        {
            if (tag == "INAM") found = ReadZeroTerminated(b.Span);
        });
        return found;
    }

    // ── RIFF chunk traversal ────────────────────────────────────────

    private struct ChunkReader
    {
        private readonly ReadOnlyMemory<byte> _data;
        public ChunkReader(ReadOnlyMemory<byte> data) { _data = data; }

        public void ExpectRiff(string formType)
        {
            var span = _data.Span;
            if (span.Length < 12) throw new InvalidDataException("Truncated RIFF");
            if (ReadFourCc(span, 0) != "RIFF") throw new InvalidDataException("Not a RIFF file");
            if (ReadFourCc(span, 8) != formType)
                throw new InvalidDataException($"Expected RIFF form '{formType}', got '{ReadFourCc(span, 8)}'");
        }

        public void ForEachSubChunk(Action<string, ReadOnlyMemory<byte>> handler)
        {
            // For top-level RIFF, body starts after "RIFF<size><formType>" = 12 bytes.
            // For sub-LISTs, body starts after "<formType>" = 4 bytes (caller pre-slices).
            int start = 0;
            var span = _data.Span;
            if (span.Length >= 12 && ReadFourCc(span, 0) == "RIFF") start = 12;

            int pos = start;
            while (pos + 8 <= span.Length)
            {
                string tag = ReadFourCc(span, pos);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 4));
                int bodyStart = pos + 8;
                if (bodyStart + size > span.Length) break;
                handler(tag, _data.Slice(bodyStart, (int)size));
                pos = bodyStart + (int)size;
                if ((size & 1) != 0) pos++;     // pad byte on odd sizes
            }
        }
    }

    private static string ReadFourCc(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 4 > data.Length) return string.Empty;
        return Encoding.ASCII.GetString(data.Slice(offset, 4));
    }

    private static string ReadZeroTerminated(ReadOnlySpan<byte> data)
    {
        int end = 0;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data.Slice(0, end));
    }
}
