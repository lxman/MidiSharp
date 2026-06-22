using System;
using System.Collections.Generic;
using System.IO;
using MidiSharp.Loader.Dls;
using MidiSharp.Loader.Format;
using MidiSharp.Loader.Sf2;
using MidiSharp.Loader.Sf3;
using MidiSharp.Loader.Sfz;
using MidiSharp.SoundBank;
using IRBank = MidiSharp.SoundBank.SoundBank;
namespace MidiSharp.Loader;

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
    public static IRBank Load(string path, SoundBankLoadOptions? options = null)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path must be non-empty", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"Sound bank file not found: {path}", path);

        SoundBankFormat format = FormatDetector.DetectFromFile(path);
        options ??= new SoundBankLoadOptions();

        try
        {
            return format switch
            {
                SoundBankFormat.Sf2 => LoadSf2(path, options),
                SoundBankFormat.Sf3 => Sf3BankLoader.Load(SoundFont.Load(path), options),
                SoundBankFormat.Sfz => SfzBankLoader.Load(path, options),
                SoundBankFormat.Dls => DlsBankLoader.Load(DlsReader.Load(path), options),
                _ => throw new UnsupportedFormatException($"Unknown format: {format}"),
            };
        }
        catch (SoundFontException ex)
        {
            throw new SoundBankLoadException($"Failed to parse SF2 file '{path}': {ex.Message}", ex);
        }
    }

    // SF2 load. When MemoryMapSamples is set (and the file fits a 32-bit span), the sample pool is
    // backed by a read-only memory-mapped view instead of a managed byte[], keeping it off the GC
    // heap. The view's owner is handed to the bank's sample source, which disposes it (after the
    // audio output has stopped). Falls back to a managed read on opt-out or for >2 GB files.
    private static IRBank LoadSf2(string path, SoundBankLoadOptions options)
    {
        if (options.MemoryMapSamples && new FileInfo(path).Length <= int.MaxValue)
        {
            MemoryMappedFileManager? owner = null;
            try
            {
                owner = new MemoryMappedFileManager(path);
                SoundFont sf = SoundFont.Load(owner.Memory);
                return Sf2BankLoader.Load(sf, options, owner);   // bank takes ownership of the view
            }
            catch
            {
                ((IDisposable?)owner)?.Dispose();
                throw;
            }
        }

        return Sf2BankLoader.Load(SoundFont.Load(path), options);
    }

    /// <summary>
    /// Combine several SFZ files into one bank, each placed on its own MIDI bank
    /// number. The standard use is a GM bank split into melodic and percussion
    /// files: load the melodic file on bank 0 and the drum file on bank 128 (where
    /// the synth routes MIDI channel 10), e.g.
    /// <c>LoadSfz(new[] {(melodic, 0), (drums, 128)})</c>. Samples are pooled and
    /// de-duplicated across the files.
    /// </summary>
    public static IRBank LoadSfz(
        IReadOnlyList<(string Path, int Bank)> sfzFiles, SoundBankLoadOptions? options = null)
    {
        if (sfzFiles == null || sfzFiles.Count == 0)
            throw new ArgumentException("At least one SFZ file is required", nameof(sfzFiles));
        foreach ((string path, int _) in sfzFiles)
            if (!File.Exists(path)) throw new FileNotFoundException($"SFZ file not found: {path}", path);

        return SfzBankLoader.LoadCombined(sfzFiles, options ?? new SoundBankLoadOptions());
    }

    /// <summary>
    /// Load from an already-open stream. <paramref name="format"/> is required
    /// (no magic-byte sniffing on arbitrary streams). <paramref name="basePath"/>
    /// is used by SFZ for resolving relative sample references; ignored by
    /// other formats.
    /// </summary>
    public static IRBank Load(
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
                byte[] bytes = ReadAllBytes(stream);
                try
                {
                    return Sf2BankLoader.Load(SoundFont.Load(bytes), options);
                }
                catch (SoundFontException ex)
                {
                    throw new SoundBankLoadException($"Failed to parse SF2 stream: {ex.Message}", ex);
                }
            }
            case SoundBankFormat.Sf3:
            {
                byte[] bytes = ReadAllBytes(stream);
                try
                {
                    return Sf3BankLoader.Load(SoundFont.Load(bytes), options);
                }
                catch (SoundFontException ex)
                {
                    throw new SoundBankLoadException($"Failed to parse SF3 stream: {ex.Message}", ex);
                }
            }
            case SoundBankFormat.Sfz:
                return SfzBankLoader.Load(stream, basePath, options);
            case SoundBankFormat.Dls:
            {
                byte[] bytes = ReadAllBytes(stream);
                return DlsBankLoader.Load(DlsReader.Load(bytes), options);
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
