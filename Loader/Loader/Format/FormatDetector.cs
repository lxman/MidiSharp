using System;
using System.IO;

namespace MidiSharp.SoundBank.Format;

/// <summary>
/// Magic-byte sniffing for the four supported sound-bank formats. Extension is
/// consulted only as a tiebreaker for SF2/SF3 (both are RIFF/sfbk; the file
/// suffix is what distinguishes them).
/// </summary>
internal static class FormatDetector
{
    /// <summary>
    /// Inspect a path's first 12 bytes plus its extension to decide the format.
    /// Throws <see cref="UnsupportedFormatException"/> if no rule matches.
    /// </summary>
    public static SoundBankFormat DetectFromFile(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        int read = fs.Read(header);
        return Detect(header.Slice(0, read), path);
    }

    /// <summary>
    /// Inspect a header buffer (first ≥4 bytes) and an optional path hint.
    /// </summary>
    public static SoundBankFormat Detect(ReadOnlySpan<byte> header, string? pathHint)
    {
        // SFZ is plain text — no binary magic, dispatch purely on extension.
        if (pathHint != null && pathHint.EndsWith(".sfz", StringComparison.OrdinalIgnoreCase))
            return SoundBankFormat.Sfz;

        if (header.Length >= 12 && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F')
        {
            // RIFF form-type at bytes 8..11.
            var form = header.Slice(8, 4);

            if (Match(form, "sfbk"))
            {
                // SF2 and SF3 share the sfbk form-type. Extension breaks the tie.
                if (pathHint != null && pathHint.EndsWith(".sf3", StringComparison.OrdinalIgnoreCase))
                    return SoundBankFormat.Sf3;
                return SoundBankFormat.Sf2;
            }

            if (Match(form, "DLS "))
                return SoundBankFormat.Dls;
        }

        throw new UnsupportedFormatException(
            pathHint != null
                ? $"Cannot determine sound-bank format from '{pathHint}'"
                : "Cannot determine sound-bank format from header bytes");
    }

    private static bool Match(ReadOnlySpan<byte> bytes, string ascii)
    {
        if (bytes.Length != ascii.Length) return false;
        for (int i = 0; i < ascii.Length; i++)
            if (bytes[i] != (byte)ascii[i]) return false;
        return true;
    }
}
