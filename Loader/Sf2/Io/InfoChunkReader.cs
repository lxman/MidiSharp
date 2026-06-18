using System;
using MidiSharp.Loader.Sf2.Model;

namespace MidiSharp.Loader.Sf2.Io;

/// <summary>
/// Walks the sub-chunks of an INFO LIST and decodes them into <see cref="InfoMetadata"/>.
/// </summary>
internal static class InfoChunkReader
{
    public static InfoMetadata Read(ReadOnlyMemory<byte> infoList)
    {
        var span = infoList.Span;
        if (BinaryHelpers.ReadTag(span, 0) != "INFO")
            throw new SoundFontException(SoundFontValidationCode.FileBroken, "Expected INFO form type");

        var info = new InfoMetadata();
        var sawIfil = false;

        var pos = 4;
        while (pos + 8 <= span.Length)
        {
            var tag = BinaryHelpers.ReadTag(span, pos);
            var size = BinaryHelpers.ReadUInt32LE(span, pos + 4);
            var bodyStart = pos + 8;
            if (bodyStart + size > span.Length)
                throw new SoundFontException(SoundFontValidationCode.FileBroken);
            var body = span.Slice(bodyStart, (int)size);

            switch (tag)
            {
                case "ifil":
                    if (size != 4)
                        throw new SoundFontException(SoundFontValidationCode.IfilBadLength);
                    info.SpecVersion = new VersionTag(
                        BinaryHelpers.ReadUInt16LE(body, 0),
                        BinaryHelpers.ReadUInt16LE(body, 2));
                    sawIfil = true;
                    break;
                case "iver":
                    if (size == 4)
                        info.RomVersion = new VersionTag(
                            BinaryHelpers.ReadUInt16LE(body, 0),
                            BinaryHelpers.ReadUInt16LE(body, 2));
                    break;
                case "isng": info.SoundEngine = DecodeZString(body); break;
                case "INAM": info.BankName = DecodeZString(body); break;
                case "irom": info.RomName = DecodeZString(body); break;
                case "ICRD": info.CreationDate = DecodeZString(body); break;
                case "IENG": info.Engineer = DecodeZString(body); break;
                case "IPRD": info.Product = DecodeZString(body); break;
                case "ICOP": info.Copyright = DecodeZString(body); break;
                case "ICMT": info.Comments = DecodeZString(body); break;
                case "ISFT": info.Software = DecodeZString(body); break;
            }

            pos = bodyStart + (int)size;
            if ((size & 1) != 0) pos++;
        }

        if (!sawIfil)
            throw new SoundFontException(SoundFontValidationCode.IfilMissing);
        // The original sflib also enforces isng + INAM, but in practice many real-world
        // SoundFonts omit them. Match the C++ runtime behavior (which records the error
        // but does not abort the load) by accepting them as warnings; consumers can still
        // detect them via empty strings on the result.

        return info;
    }

    private static string DecodeZString(ReadOnlySpan<byte> body)
    {
        var nul = body.IndexOf((byte)0);
        if (nul >= 0) body = body.Slice(0, nul);
        return BinaryHelpers.Ascii.GetString(body.ToArray());
    }
}
