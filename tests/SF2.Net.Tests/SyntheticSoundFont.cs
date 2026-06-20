using System.Buffers.Binary;
using System.Text;

namespace SF2.Net.Tests;

/// <summary>
/// Builds a minimal, spec-compliant SF2 file in memory for testing — one preset (bank 0, preset 0)
/// pointing at one instrument with one zone referencing one sample.
/// </summary>
internal static class SyntheticSoundFont
{
    private const int SampleFrames = 1024; // sine sweep length

    public static byte[] Build(
        string bankName = "Test Bank",
        string presetName = "TestPiano",
        string instrumentName = "TestInst",
        string sampleName = "TestSample",
        ushort bank = 0,
        ushort preset = 0,
        uint sampleRate = 22050,
        string terminalName = "EOP",
        byte[]? sm24 = null)
    {
        // ----- smpl (16-bit LE PCM) ----
        var smpl = new byte[SampleFrames * 2];
        for (var i = 0; i < SampleFrames; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * i / 64.0) * 16000);
            BinaryPrimitives.WriteInt16LittleEndian(smpl.AsSpan(i * 2, 2), v);
        }

        // ----- shdr: one sample + EOS ----
        uint sampleStart = 0;
        uint sampleEnd = SampleFrames;
        var shdr = new byte[46 * 2];
        WriteShdr(shdr.AsSpan(0, 46), sampleName, sampleStart, sampleEnd,
            sampleStart + 8, sampleEnd - 8, sampleRate, 60, 0, 0, 1 /* mono */);
        WriteShdr(shdr.AsSpan(46, 46), "EOS", sampleEnd, sampleEnd, sampleEnd, sampleEnd, 0, 0, 0, 0, 1);

        // ----- igen: one generator (sampleID=0) + terminal generator ----
        var igen = new byte[4 * 2];
        WriteGen(igen.AsSpan(0, 4), 53 /*sampleID*/, 0);
        WriteGen(igen.AsSpan(4, 4), 0, 0);

        // ----- imod: just a terminal record ----
        var imod = new byte[10];
        // already zeroed

        // ----- ibag: one zone bag + terminal ----
        var ibag = new byte[4 * 2];
        WriteBag(ibag.AsSpan(0, 4), 0, 0);   // first zone uses gen 0..1, mod 0..0
        WriteBag(ibag.AsSpan(4, 4), 1, 0);   // terminal points past

        // ----- inst: one instrument + terminal EOI.
        // EOI.BagIdx must point at the terminal ibag (index 1 here), not past it.
        var inst = new byte[22 * 2];
        WriteInst(inst.AsSpan(0, 22), instrumentName, 0);
        WriteInst(inst.AsSpan(22, 22), "EOI", 1);

        // ----- pgen: instrument=0 generator + terminal ----
        var pgen = new byte[4 * 2];
        WriteGen(pgen.AsSpan(0, 4), 41 /*instrument*/, 0);
        WriteGen(pgen.AsSpan(4, 4), 0, 0);

        // ----- pmod: terminal only ----
        var pmod = new byte[10];

        // ----- pbag: one preset zone + terminal ----
        var pbag = new byte[4 * 2];
        WriteBag(pbag.AsSpan(0, 4), 0, 0);
        WriteBag(pbag.AsSpan(4, 4), 1, 0);

        // ----- phdr: one preset + EOP terminal.
        // EOP.BagIdx points at the terminal pbag (index 1 here).
        var phdr = new byte[38 * 2];
        WritePhdr(phdr.AsSpan(0, 38), presetName, preset, bank, 0, 0, 0, 0);
        WritePhdr(phdr.AsSpan(38, 38), terminalName, 0, 0, 1, 0, 0, 0);

        // ----- Wrap chunks ----
        var pdta = BuildList("pdta", [
            ("phdr", phdr),
            ("pbag", pbag),
            ("pmod", pmod),
            ("pgen", pgen),
            ("inst", inst),
            ("ibag", ibag),
            ("imod", imod),
            ("igen", igen),
            ("shdr", shdr)
        ]);

        // Optional 24-bit extension (one LS byte per frame), aligned 1:1 with the smpl frames.
        var sdta = sm24 is { Length: SampleFrames }
            ? BuildList("sdta", [("smpl", smpl), ("sm24", sm24)])
            : BuildList("sdta", [("smpl", smpl)]);

        // ----- INFO LIST ----
        var info = BuildInfoList(bankName);

        // ----- RIFF ----
        var riffBodyLen = 4 + info.Length + sdta.Length + pdta.Length;
        var riff = new MemoryStream();
        riff.Write(Encoding.ASCII.GetBytes("RIFF"), 0, 4);
        WriteU32(riff, (uint)riffBodyLen);
        riff.Write(Encoding.ASCII.GetBytes("sfbk"), 0, 4);
        riff.Write(info, 0, info.Length);
        riff.Write(sdta, 0, sdta.Length);
        riff.Write(pdta, 0, pdta.Length);
        return riff.ToArray();
    }

    // ---- INFO LIST builder ----
    private static byte[] BuildInfoList(string bankName)
    {
        var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes("INFO"), 0, 4);
        // ifil
        body.Write(Encoding.ASCII.GetBytes("ifil"), 0, 4); WriteU32(body, 4);
        WriteU16(body, 2); WriteU16(body, 1); // 2.1
        // isng
        WriteStringChunk(body, "isng", "EMU8000");
        // INAM
        WriteStringChunk(body, "INAM", bankName);

        var inner = body.ToArray();
        var list = new MemoryStream();
        list.Write(Encoding.ASCII.GetBytes("LIST"), 0, 4);
        WriteU32(list, (uint)inner.Length);
        list.Write(inner, 0, inner.Length);
        if ((inner.Length & 1) != 0) list.WriteByte(0);
        return list.ToArray();
    }

    private static void WriteStringChunk(MemoryStream ms, string tag, string value)
    {
        ms.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
        var str = Encoding.ASCII.GetBytes(value);
        var len = str.Length + 1;
        if ((len & 1) != 0) len++;
        WriteU32(ms, (uint)len);
        ms.Write(str, 0, str.Length);
        ms.WriteByte(0);
        if ((str.Length + 1) % 2 != 0) ms.WriteByte(0);
    }

    private static byte[] BuildList(string formType, (string, byte[])[] children)
    {
        var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes(formType), 0, 4);
        foreach (var (tag, data) in children)
        {
            body.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
            WriteU32(body, (uint)data.Length);
            body.Write(data, 0, data.Length);
            if ((data.Length & 1) != 0) body.WriteByte(0);
        }
        var inner = body.ToArray();
        var list = new MemoryStream();
        list.Write(Encoding.ASCII.GetBytes("LIST"), 0, 4);
        WriteU32(list, (uint)inner.Length);
        list.Write(inner, 0, inner.Length);
        if ((inner.Length & 1) != 0) list.WriteByte(0);
        return list.ToArray();
    }

    private static void WriteU16(MemoryStream ms, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        ms.Write(b.ToArray(), 0, 2);
    }

    private static void WriteU32(MemoryStream ms, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        ms.Write(b.ToArray(), 0, 4);
    }

    private static void WriteFixedAscii(Span<byte> dest, string value)
    {
        dest.Clear();
        var n = Math.Min(value.Length, dest.Length);
        for (var i = 0; i < n; i++) dest[i] = (byte)value[i];
        if (value.Length >= dest.Length) dest[^1] = 0;
    }

    private static void WriteShdr(Span<byte> dest, string name, uint start, uint end,
        uint startLoop, uint endLoop, uint rate, byte pitch, sbyte corr, ushort link, ushort type)
    {
        WriteFixedAscii(dest.Slice(0, 20), name);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(20, 4), start);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(24, 4), end);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(28, 4), startLoop);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(32, 4), endLoop);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(36, 4), rate);
        dest[40] = pitch;
        dest[41] = (byte)corr;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(42, 2), link);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(44, 2), type);
    }

    private static void WriteGen(Span<byte> dest, ushort op, ushort amount)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(0, 2), op);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(2, 2), amount);
    }

    private static void WriteBag(Span<byte> dest, ushort genNdx, ushort modNdx)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(0, 2), genNdx);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(2, 2), modNdx);
    }

    private static void WriteInst(Span<byte> dest, string name, ushort bagIdx)
    {
        WriteFixedAscii(dest.Slice(0, 20), name);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(20, 2), bagIdx);
    }

    private static void WritePhdr(Span<byte> dest, string name, ushort preset, ushort bank,
        ushort bagIdx, uint library, uint genre, uint morphology)
    {
        WriteFixedAscii(dest.Slice(0, 20), name);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(20, 2), preset);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(22, 2), bank);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(24, 2), bagIdx);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(26, 4), library);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(30, 4), genre);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(34, 4), morphology);
    }
}
