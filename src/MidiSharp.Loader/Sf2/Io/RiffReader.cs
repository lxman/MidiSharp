using System;

namespace MidiSharp.Loader.Sf2.Io;

/// <summary>
/// Splits the top-level RIFF/sfbk container into its three LIST chunks (INFO, sdta, pdta).
/// </summary>
internal sealed class RiffReader
{
    public ReadOnlyMemory<byte> Info { get; }
    public ReadOnlyMemory<byte> Sdta { get; }
    public ReadOnlyMemory<byte> Pdta { get; }

    public RiffReader(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;
        if (span.Length < 12)
            throw new SoundFontException(SoundFontValidationCode.RiffChunkTooSmall);

        if (BinaryHelpers.ReadTag(span, 0) != "RIFF")
            throw new SoundFontException(SoundFontValidationCode.FileBroken, "Missing RIFF header");

        // The RIFF size field is often a lie in real-world files (sometimes much smaller,
        // sometimes much larger than the actual data). The sub-chunk sizes are the source
        // of truth, so we ignore the RIFF size and walk the actual file bytes until we've
        // collected the three required LIST chunks. This survives both truncated files
        // (where we'll error on a chunk that overruns) and files with trailing junk.
        int remaining = span.Length - 8;
        ReadOnlyMemory<byte> main = data.Slice(8, remaining);
        ReadOnlySpan<byte> mainSpan = main.Span;
        if (BinaryHelpers.ReadTag(mainSpan, 0) != "sfbk")
            throw new SoundFontException(SoundFontValidationCode.FileBroken, "Expected sfbk form type");

        var pos = 4;
        ReadOnlyMemory<byte>? info = null, sdta = null, pdta = null;
        while (pos + 8 <= mainSpan.Length && (info is null || sdta is null || pdta is null))
        {
            string tag = BinaryHelpers.ReadTag(mainSpan, pos);
            uint size = BinaryHelpers.ReadUInt32LE(mainSpan, pos + 4);
            pos += 8;
            if (pos + size > mainSpan.Length)
                throw new SoundFontException(SoundFontValidationCode.FileBroken);
            if (tag != "LIST")
                throw new SoundFontException(SoundFontValidationCode.FileBroken, $"Expected LIST, got '{tag}'");

            ReadOnlyMemory<byte> listBody = main.Slice(pos, (int)size);
            string formType = BinaryHelpers.ReadTag(listBody.Span, 0);
            switch (formType)
            {
                case "INFO": info = listBody; break;
                case "sdta": sdta = listBody; break;
                case "pdta": pdta = listBody; break;
            }
            pos += (int)size;
            if ((size & 1) != 0 && pos < mainSpan.Length) pos++; // RIFF pad byte
        }

        if (info is null || sdta is null || pdta is null)
            throw new SoundFontException(SoundFontValidationCode.FileBroken, "Missing one or more LIST chunks");

        Info = info.Value;
        Sdta = sdta.Value;
        Pdta = pdta.Value;
    }
}
