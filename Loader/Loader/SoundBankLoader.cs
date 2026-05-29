using System;
using System.Collections.Generic;
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
                SoundBankFormat.Sf3 => Sf3.Sf3BankLoader.Load(SF2.Net.SoundFont.Load(path), options),
                SoundBankFormat.Sfz => Sfz.SfzBankLoader.Load(path, options),
                SoundBankFormat.Dls => Dls.DlsBankLoader.Load(DLS.Net.DlsReader.Load(path), options),
                _ => throw new UnsupportedFormatException($"Unknown format: {format}"),
            };
        }
        catch (SF2.Net.SoundFontException ex)
        {
            throw new SoundBankLoadException($"Failed to parse SF2 file '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Combine several SFZ files into one bank, each placed on its own MIDI bank
    /// number. The standard use is a GM bank split into melodic and percussion
    /// files: load the melodic file on bank 0 and the drum file on bank 128 (where
    /// the synth routes MIDI channel 10), e.g.
    /// <c>LoadSfz(new[] {(melodic, 0), (drums, 128)})</c>. Samples are pooled and
    /// de-duplicated across the files.
    /// </summary>
    public static SoundBank LoadSfz(
        IReadOnlyList<(string Path, int Bank)> sfzFiles, SoundBankLoadOptions? options = null)
    {
        if (sfzFiles == null || sfzFiles.Count == 0)
            throw new ArgumentException("At least one SFZ file is required", nameof(sfzFiles));
        foreach (var (path, _) in sfzFiles)
            if (!File.Exists(path)) throw new FileNotFoundException($"SFZ file not found: {path}", path);

        return Sfz.SfzBankLoader.LoadCombined(sfzFiles, options ?? new SoundBankLoadOptions());
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
            {
                var bytes = ReadAllBytes(stream);
                try
                {
                    return Sf3.Sf3BankLoader.Load(SF2.Net.SoundFont.Load(bytes), options);
                }
                catch (SF2.Net.SoundFontException ex)
                {
                    throw new SoundBankLoadException($"Failed to parse SF3 stream: {ex.Message}", ex);
                }
            }
            case SoundBankFormat.Sfz:
                return Sfz.SfzBankLoader.Load(stream, basePath, options);
            case SoundBankFormat.Dls:
            {
                var bytes = ReadAllBytes(stream);
                return Dls.DlsBankLoader.Load(DLS.Net.DlsReader.Load(bytes), options);
            }
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
