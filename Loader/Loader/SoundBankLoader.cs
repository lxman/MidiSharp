using System;
using System.IO;
using MidiSharp.SoundBank.Format;
using MidiSharp.SoundBank.Sf2;

namespace MidiSharp.SoundBank;

/// <summary>
/// The public entry point for loading any supported sound-bank format into
/// the IR. Callers don't need to know which format their file is; the loader
/// detects via magic bytes (and extension as tiebreaker for SF2 vs SF3).
/// </summary>
public static class SoundBankLoader
{
    /// <summary>
    /// Load a sound bank from a file path. The format is detected from the
    /// file's first 12 bytes and its extension.
    /// </summary>
    /// <exception cref="UnsupportedFormatException">
    /// The file's bytes don't match any known sound-bank format.
    /// </exception>
    /// <exception cref="SoundBankLoadException">
    /// The format was recognized but the file is malformed (corrupt chunks,
    /// missing referenced samples, etc.).
    /// </exception>
    public static SoundBank Load(string path, SoundBankLoadOptions? options = null)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path must be non-empty", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"Sound bank file not found: {path}", path);

        var format = FormatDetector.DetectFromFile(path);
        options ??= new SoundBankLoadOptions();

        try
        {
            return format switch
            {
                SoundBankFormat.Sf2 => Sf2BankLoader.Load(SF2.Net.SoundFont.Load(path), options),
                SoundBankFormat.Sf3 => throw new NotSupportedException("SF3 loader not yet implemented"),
                SoundBankFormat.Sfz => throw new NotSupportedException("SFZ loader not yet implemented"),
                SoundBankFormat.Dls => throw new NotSupportedException("DLS loader not yet implemented"),
                _ => throw new UnsupportedFormatException($"Unknown format: {format}"),
            };
        }
        catch (SF2.Net.SoundFontException ex)
        {
            throw new SoundBankLoadException($"Failed to parse SF2 file '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Load from an already-open stream. <paramref name="format"/> is required
    /// (no magic-byte sniffing on arbitrary streams). <paramref name="basePath"/>
    /// is used by SFZ for resolving relative sample references; ignored by
    /// other formats.
    /// </summary>
    public static SoundBank Load(
        Stream stream,
        SoundBankFormat format,
        string? basePath = null,
        SoundBankLoadOptions? options = null)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        options ??= new SoundBankLoadOptions();

        switch (format)
        {
            case SoundBankFormat.Sf2:
            {
                var bytes = ReadAllBytes(stream);
                try
                {
                    return Sf2BankLoader.Load(SF2.Net.SoundFont.Load(bytes), options);
                }
                catch (SF2.Net.SoundFontException ex)
                {
                    throw new SoundBankLoadException($"Failed to parse SF2 stream: {ex.Message}", ex);
                }
            }
            case SoundBankFormat.Sf3:
                throw new NotSupportedException("SF3 loader not yet implemented");
            case SoundBankFormat.Sfz:
                throw new NotSupportedException("SFZ loader not yet implemented");
            case SoundBankFormat.Dls:
                throw new NotSupportedException("DLS loader not yet implemented");
            default:
                throw new UnsupportedFormatException($"Unknown format: {format}");
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream is MemoryStream ms) return ms.ToArray();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
