namespace SF2.Net.Io;

internal static class Assemblers
{
    /// <summary>Writes a 38-byte phdr record.</summary>
    public static void WritePhdr(Span<byte> dest, PresetHeaderRecord r)
    {
        BinaryHelpers.WriteFixedAscii(dest, 0, 20, r.Name);
        BinaryHelpers.WriteUInt16LE(dest, 20, r.Preset);
        BinaryHelpers.WriteUInt16LE(dest, 22, r.Bank);
        BinaryHelpers.WriteUInt16LE(dest, 24, r.BagIndex);
        BinaryHelpers.WriteUInt32LE(dest, 26, r.Library);
        BinaryHelpers.WriteUInt32LE(dest, 30, r.Genre);
        BinaryHelpers.WriteUInt32LE(dest, 34, r.Morphology);
    }

    /// <summary>Writes a 4-byte bag record.</summary>
    public static void WriteBag(Span<byte> dest, BagRecord r)
    {
        BinaryHelpers.WriteUInt16LE(dest, 0, r.GenIndex);
        BinaryHelpers.WriteUInt16LE(dest, 2, r.ModIndex);
    }

    /// <summary>Writes a 10-byte modulator record.</summary>
    public static void WriteMod(Span<byte> dest, Modulator m)
    {
        BinaryHelpers.WriteUInt16LE(dest, 0, m.SourceOperator);
        BinaryHelpers.WriteUInt16LE(dest, 2, (ushort)m.DestinationOperator);
        BinaryHelpers.WriteInt16LE(dest, 4, m.Amount);
        BinaryHelpers.WriteUInt16LE(dest, 6, m.AmountSourceOperator);
        BinaryHelpers.WriteUInt16LE(dest, 8, m.TransformOperator);
    }

    /// <summary>Writes a 4-byte generator record.</summary>
    public static void WriteGen(Span<byte> dest, Generator g)
    {
        BinaryHelpers.WriteUInt16LE(dest, 0, (ushort)g.Operator);
        BinaryHelpers.WriteUInt16LE(dest, 2, g.Amount.Word);
    }

    /// <summary>Writes a 22-byte inst record.</summary>
    public static void WriteInst(Span<byte> dest, InstrumentRecord r)
    {
        BinaryHelpers.WriteFixedAscii(dest, 0, 20, r.Name);
        BinaryHelpers.WriteUInt16LE(dest, 20, r.BagIndex);
    }

    /// <summary>Writes a 46-byte shdr record.</summary>
    public static void WriteShdr(Span<byte> dest, SampleHeader s)
    {
        BinaryHelpers.WriteFixedAscii(dest, 0, 20, s.Name);
        BinaryHelpers.WriteUInt32LE(dest, 20, s.Start);
        BinaryHelpers.WriteUInt32LE(dest, 24, s.End);
        BinaryHelpers.WriteUInt32LE(dest, 28, s.StartLoop);
        BinaryHelpers.WriteUInt32LE(dest, 32, s.EndLoop);
        BinaryHelpers.WriteUInt32LE(dest, 36, s.SampleRate);
        dest[40] = s.OriginalPitch;
        dest[41] = (byte)s.PitchCorrection;
        BinaryHelpers.WriteUInt16LE(dest, 42, s.SampleLink);
        BinaryHelpers.WriteUInt16LE(dest, 44, (ushort)s.SampleType);
    }
}

/// <summary>
/// Builds an INFO LIST chunk body from an <see cref="InfoMetadata"/>.
/// </summary>
internal static class InfoAssembler
{
    public static byte[] Build(InfoMetadata info, string? overrideBankName = null)
    {
        using var ms = new MemoryStream();
        WriteFourByteTag(ms, "ifil");
        WriteUInt32(ms, 4);
        WriteUInt16(ms, info.SpecVersion.Major);
        WriteUInt16(ms, info.SpecVersion.Minor);

        WriteStringField(ms, "isng", Truncate(info.SoundEngine, 256));
        WriteStringField(ms, "INAM", Truncate(overrideBankName ?? info.BankName, 256));
        if (!string.IsNullOrEmpty(info.RomName))
            WriteStringField(ms, "irom", Truncate(info.RomName!, 256));

        if (info.RomVersion is { } iv)
        {
            WriteFourByteTag(ms, "iver");
            WriteUInt32(ms, 4);
            WriteUInt16(ms, iv.Major);
            WriteUInt16(ms, iv.Minor);
        }

        if (!string.IsNullOrEmpty(info.CreationDate)) WriteStringField(ms, "ICRD", Truncate(info.CreationDate!, 256));
        if (!string.IsNullOrEmpty(info.Engineer))     WriteStringField(ms, "IENG", Truncate(info.Engineer!, 256));
        if (!string.IsNullOrEmpty(info.Product))      WriteStringField(ms, "IPRD", Truncate(info.Product!, 256));
        if (!string.IsNullOrEmpty(info.Copyright))    WriteStringField(ms, "ICOP", Truncate(info.Copyright!, 256));
        if (!string.IsNullOrEmpty(info.Comments))     WriteStringField(ms, "ICMT", Truncate(info.Comments!, 65536));
        if (!string.IsNullOrEmpty(info.Software))     WriteStringField(ms, "ISFT", Truncate(info.Software!, 256));

        return ms.ToArray();
    }

    private static string Truncate(string s, int max) => s.Length > max ? s.Substring(0, max) : s;

    private static void WriteFourByteTag(MemoryStream ms, string tag)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryHelpers.WriteFixedAscii(buf, 0, 4, tag);
        ms.Write(buf.ToArray(), 0, 4);
    }

    private static void WriteUInt32(MemoryStream ms, uint v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryHelpers.WriteUInt32LE(buf, 0, v);
        ms.Write(buf.ToArray(), 0, 4);
    }

    private static void WriteUInt16(MemoryStream ms, ushort v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryHelpers.WriteUInt16LE(buf, 0, v);
        ms.Write(buf.ToArray(), 0, 2);
    }

    private static void WriteStringField(MemoryStream ms, string tag, string value)
    {
        WriteFourByteTag(ms, tag);
        var bytes = BinaryHelpers.Ascii.GetBytes(value);
        // Always zero-terminate, then pad to even length.
        var len = bytes.Length + 1;
        if ((len & 1) != 0) len++;
        WriteUInt32(ms, (uint)len);
        ms.Write(bytes, 0, bytes.Length);
        ms.WriteByte(0);
        if ((bytes.Length + 1) % 2 != 0) ms.WriteByte(0);
    }
}
