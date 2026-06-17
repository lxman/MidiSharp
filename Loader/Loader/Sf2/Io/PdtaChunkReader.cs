namespace SF2.Net.Io;

/// <summary>
/// Splits a pdta LIST into its nine fixed sub-chunks. The SF2 spec requires that they appear
/// in the order phdr, pbag, pmod, pgen, inst, ibag, imod, igen, shdr — we enforce that here.
/// </summary>
internal sealed class PdtaChunkReader
{
    public ReadOnlyMemory<byte> Phdr { get; }
    public ReadOnlyMemory<byte> Pbag { get; }
    public ReadOnlyMemory<byte> Pmod { get; }
    public ReadOnlyMemory<byte> Pgen { get; }
    public ReadOnlyMemory<byte> Inst { get; }
    public ReadOnlyMemory<byte> Ibag { get; }
    public ReadOnlyMemory<byte> Imod { get; }
    public ReadOnlyMemory<byte> Igen { get; }
    public ReadOnlyMemory<byte> Shdr { get; }

    public PdtaChunkReader(ReadOnlyMemory<byte> pdtaList)
    {
        var span = pdtaList.Span;
        if (BinaryHelpers.ReadTag(span, 0) != "pdta")
            throw new SoundFontException(SoundFontValidationCode.FileBroken, "Expected pdta form type");

        var pos = 4;
        // SF2 spec requires each chunk to contain at least the terminal record (one fixed-size
        // record). Some real-world files have zero presets or zero samples but are otherwise
        // well-formed (e.g. sample-library exports); we accept those.
        Phdr = ReadFixed(pdtaList, ref pos, "phdr", 38, SoundFontValidationCode.PhdrChunkBad, minMultiples: 1);
        Pbag = ReadFixed(pdtaList, ref pos, "pbag", 4,  SoundFontValidationCode.PbagChunkBad, minMultiples: 1);
        Pmod = ReadFixed(pdtaList, ref pos, "pmod", 10, SoundFontValidationCode.PmodChunkBad, minMultiples: 1);
        Pgen = ReadFixed(pdtaList, ref pos, "pgen", 4,  SoundFontValidationCode.PgenChunkBad, minMultiples: 1);
        Inst = ReadFixed(pdtaList, ref pos, "inst", 22, SoundFontValidationCode.InstChunkBad, minMultiples: 1);
        Ibag = ReadFixed(pdtaList, ref pos, "ibag", 4,  SoundFontValidationCode.IbagChunkBad, minMultiples: 1);
        Imod = ReadFixed(pdtaList, ref pos, "imod", 10, SoundFontValidationCode.ImodChunkBad, minMultiples: 1);
        Igen = ReadFixed(pdtaList, ref pos, "igen", 4,  SoundFontValidationCode.IgenChunkBad, minMultiples: 1);
        Shdr = ReadFixed(pdtaList, ref pos, "shdr", 46, SoundFontValidationCode.ShdrChunkBad, minMultiples: 1);
    }

    private static ReadOnlyMemory<byte> ReadFixed(
        ReadOnlyMemory<byte> pdta, ref int pos, string expectedTag,
        int recordSize, SoundFontValidationCode badCode, int minMultiples)
    {
        var span = pdta.Span;
        if (pos + 8 > span.Length)
            throw new SoundFontException(SoundFontValidationCode.FileBroken);
        var tag = BinaryHelpers.ReadTag(span, pos);
        var size = BinaryHelpers.ReadUInt32LE(span, pos + 4);
        if (tag != expectedTag)
            throw new SoundFontException(SoundFontValidationCode.FileBroken, $"Expected '{expectedTag}', got '{tag}'");
        var bodyStart = pos + 8;
        if (bodyStart + size > span.Length)
            throw new SoundFontException(SoundFontValidationCode.FileBroken);
        if (size % recordSize != 0 || size < (uint)(recordSize * minMultiples))
            throw new SoundFontException(badCode);

        pos = bodyStart + (int)size;
        if ((size & 1) != 0) pos++;
        return pdta.Slice(bodyStart, (int)size);
    }
}
