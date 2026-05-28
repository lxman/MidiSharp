using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MidiSharp.Sf2;

/// <summary>
/// Writes SoundFont 2.0 (SF2) files.
/// </summary>
public sealed class Sf2Writer
{
    private readonly List<Preset> _presets = [];
    private readonly List<Instrument> _instruments = [];
    private readonly List<SampleHeader> _sampleHeaders = [];
    private short[]? _sampleData;
    private Sf2Info _info = new();

    /// <summary>
    /// Gets or sets the SF2 info/metadata.
    /// </summary>
    public Sf2Info Info
    {
        get => _info;
        set => _info = value ?? new Sf2Info();
    }

    /// <summary>
    /// Gets the presets in the SoundFont.
    /// </summary>
    public IReadOnlyList<Preset> Presets => _presets;

    /// <summary>
    /// Gets the instruments in the SoundFont.
    /// </summary>
    public IReadOnlyList<Instrument> Instruments => _instruments;

    /// <summary>
    /// Gets the sample headers.
    /// </summary>
    public IReadOnlyList<SampleHeader> SampleHeaders => _sampleHeaders;

    /// <summary>
    /// Adds a preset to the SoundFont.
    /// </summary>
    public void AddPreset(Preset preset)
    {
        _presets.Add(preset);
    }

    /// <summary>
    /// Adds an instrument to the SoundFont.
    /// </summary>
    public void AddInstrument(Instrument instrument)
    {
        instrument.Index = _instruments.Count;
        _instruments.Add(instrument);
    }

    /// <summary>
    /// Adds a sample with its audio data.
    /// </summary>
    public void AddSample(SampleHeader header, short[] data)
    {
        header.Index = _sampleHeaders.Count;
        _sampleHeaders.Add(header);

        // Append sample data
        if (_sampleData == null)
        {
            header.Start = 0;
            header.End = (uint)data.Length;
            // Adjust loop points
            header.StartLoop = header.Start + (header.StartLoop - header.Start);
            header.EndLoop = header.Start + (header.EndLoop - header.Start);
            _sampleData = data;
        }
        else
        {
            var startOffset = (uint)_sampleData.Length;
            header.Start = startOffset;
            header.End = startOffset + (uint)data.Length;
            header.StartLoop = startOffset + header.StartLoop;
            header.EndLoop = startOffset + header.EndLoop;

            var newData = new short[_sampleData.Length + data.Length];
            Array.Copy(_sampleData, newData, _sampleData.Length);
            Array.Copy(data, 0, newData, _sampleData.Length, data.Length);
            _sampleData = newData;
        }
    }

    /// <summary>
    /// Sets the sample data directly (for bulk operations).
    /// </summary>
    public void SetSampleData(short[] data)
    {
        _sampleData = data;
    }

    /// <summary>
    /// Writes the SF2 file to a path.
    /// </summary>
    public void WriteFile(string path)
    {
        var data = Write();
        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// Writes the SF2 file to a byte array.
    /// </summary>
    public byte[] Write()
    {
        using var ms = new MemoryStream();
        Write(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the SF2 file to a stream.
    /// </summary>
    public void Write(Stream stream)
    {
        var infoChunk = BuildInfoChunk();
        var sdtaChunk = BuildSdtaChunk();
        var pdtaChunk = BuildPdtaChunk();

        // Calculate total size
        var contentSize = 4 + infoChunk.Length + sdtaChunk.Length + pdtaChunk.Length;

        // Write RIFF header
        WriteChunkId(stream, "RIFF");
        WriteUInt32(stream, (uint)contentSize);
        WriteChunkId(stream, "sfbk");

        // Write LIST chunks
        stream.Write(infoChunk);
        stream.Write(sdtaChunk);
        stream.Write(pdtaChunk);
    }

    private byte[] BuildInfoChunk()
    {
        using var ms = new MemoryStream();

        // ifil - version (required)
        WriteSubChunk(ms, "ifil", writer =>
        {
            writer.Write(_info.Version.Major);
            writer.Write(_info.Version.Minor);
        });

        // isng - sound engine (required)
        WriteStringChunk(ms, "isng", string.IsNullOrEmpty(_info.SoundEngine) ? "EMU8000" : _info.SoundEngine);

        // INAM - bank name (required)
        WriteStringChunk(ms, "INAM", string.IsNullOrEmpty(_info.BankName) ? "Untitled" : _info.BankName);

        // Optional fields
        if (!string.IsNullOrEmpty(_info.RomName))
            WriteStringChunk(ms, "irom", _info.RomName);

        if (_info.RomVersion.Major > 0 || _info.RomVersion.Minor > 0)
        {
            WriteSubChunk(ms, "iver", writer =>
            {
                writer.Write(_info.RomVersion.Major);
                writer.Write(_info.RomVersion.Minor);
            });
        }

        if (!string.IsNullOrEmpty(_info.CreationDate))
            WriteStringChunk(ms, "ICRD", _info.CreationDate);

        if (!string.IsNullOrEmpty(_info.Engineers))
            WriteStringChunk(ms, "IENG", _info.Engineers);

        if (!string.IsNullOrEmpty(_info.Product))
            WriteStringChunk(ms, "IPRD", _info.Product);

        if (!string.IsNullOrEmpty(_info.Copyright))
            WriteStringChunk(ms, "ICOP", _info.Copyright);

        if (!string.IsNullOrEmpty(_info.Comments))
            WriteStringChunk(ms, "ICMT", _info.Comments);

        if (!string.IsNullOrEmpty(_info.Tools))
            WriteStringChunk(ms, "ISFT", _info.Tools);

        return WrapListChunk("INFO", ms.ToArray());
    }

    private byte[] BuildSdtaChunk()
    {
        using var ms = new MemoryStream();

        if (_sampleData is { Length: > 0 })
        {
            WriteSubChunk(ms, "smpl", writer =>
            {
                foreach (var sample in _sampleData)
                    writer.Write(sample);
            });
        }

        return WrapListChunk("sdta", ms.ToArray());
    }

    private byte[] BuildPdtaChunk()
    {
        using var ms = new MemoryStream();

        // Build all chunks with proper indexing
        (var phdr, var pbag, var pmod, var pgen) = BuildPresetChunks();
        (var inst, var ibag, var imod, var igen) = BuildInstrumentChunks();
        var shdr = BuildSampleHeaderChunk();

        WriteRawChunk(ms, "phdr", phdr);
        WriteRawChunk(ms, "pbag", pbag);
        WriteRawChunk(ms, "pmod", pmod);
        WriteRawChunk(ms, "pgen", pgen);
        WriteRawChunk(ms, "inst", inst);
        WriteRawChunk(ms, "ibag", ibag);
        WriteRawChunk(ms, "imod", imod);
        WriteRawChunk(ms, "igen", igen);
        WriteRawChunk(ms, "shdr", shdr);

        return WrapListChunk("pdta", ms.ToArray());
    }

    private (byte[] phdr, byte[] pbag, byte[] pmod, byte[] pgen) BuildPresetChunks()
    {
        using var phdrStream = new MemoryStream();
        using var pbagStream = new MemoryStream();
        using var pmodStream = new MemoryStream();
        using var pgenStream = new MemoryStream();

        ushort bagIndex = 0;
        ushort genIndex = 0;
        ushort modIndex = 0;

        foreach (var preset in _presets)
        {
            // Write preset header
            var raw = preset.ToRawHeader(bagIndex);
            WritePresetHeader(phdrStream, raw);

            foreach (var zone in preset.Zones)
            {
                // Write bag
                WriteBag(pbagStream, genIndex, modIndex);

                // Write generators
                foreach (var gen in zone.Generators)
                {
                    WriteGenerator(pgenStream, gen);
                    genIndex++;
                }

                // Write instrument reference if present
                if (zone.InstrumentIndex >= 0)
                {
                    WriteGenerator(pgenStream, new Generator(GeneratorType.Instrument, (ushort)zone.InstrumentIndex));
                    genIndex++;
                }

                // Write modulators
                foreach (var mod in zone.Modulators)
                {
                    WriteModulator(pmodStream, mod);
                    modIndex++;
                }

                bagIndex++;
            }
        }

        // Terminal preset header (EOP)
        var terminal = new RawPresetHeader { PresetBagIndex = bagIndex };
        terminal.SetName("EOP");
        WritePresetHeader(phdrStream, terminal);

        // Terminal bag
        WriteBag(pbagStream, genIndex, modIndex);

        // Terminal generator
        WriteGenerator(pgenStream, new Generator(GeneratorType.StartAddrsOffset, 0));

        // Terminal modulator
        WriteModulator(pmodStream, new Modulator());

        return (phdrStream.ToArray(), pbagStream.ToArray(), pmodStream.ToArray(), pgenStream.ToArray());
    }

    private (byte[] inst, byte[] ibag, byte[] imod, byte[] igen) BuildInstrumentChunks()
    {
        using var instStream = new MemoryStream();
        using var ibagStream = new MemoryStream();
        using var imodStream = new MemoryStream();
        using var igenStream = new MemoryStream();

        ushort bagIndex = 0;
        ushort genIndex = 0;
        ushort modIndex = 0;

        foreach (var instrument in _instruments)
        {
            // Write instrument header
            var raw = instrument.ToRawHeader(bagIndex);
            WriteInstrumentHeader(instStream, raw);

            foreach (var zone in instrument.Zones)
            {
                // Write bag
                WriteBag(ibagStream, genIndex, modIndex);

                // Write generators
                foreach (var gen in zone.Generators)
                {
                    WriteGenerator(igenStream, gen);
                    genIndex++;
                }

                // Write sample reference if present
                if (zone.Sample != null)
                {
                    WriteGenerator(igenStream, new Generator(GeneratorType.SampleId, (ushort)zone.Sample.Index));
                    genIndex++;
                }

                // Write modulators
                foreach (var mod in zone.Modulators)
                {
                    WriteModulator(imodStream, mod);
                    modIndex++;
                }

                bagIndex++;
            }
        }

        // Terminal instrument header (EOI)
        var terminal = new RawInstrumentHeader { InstrumentBagIndex = bagIndex };
        terminal.SetName("EOI");
        WriteInstrumentHeader(instStream, terminal);

        // Terminal bag
        WriteBag(ibagStream, genIndex, modIndex);

        // Terminal generator
        WriteGenerator(igenStream, new Generator(GeneratorType.StartAddrsOffset, 0));

        // Terminal modulator
        WriteModulator(imodStream, new Modulator());

        return (instStream.ToArray(), ibagStream.ToArray(), imodStream.ToArray(), igenStream.ToArray());
    }

    private byte[] BuildSampleHeaderChunk()
    {
        using var ms = new MemoryStream();

        foreach (var header in _sampleHeaders)
        {
            var raw = header.ToRaw();
            WriteSampleHeader(ms, raw);
        }

        // Terminal sample header (EOS)
        var terminal = new RawSampleHeader
        {
            Start = (uint)(_sampleData?.Length ?? 0),
            End = (uint)(_sampleData?.Length ?? 0),
            StartLoop = (uint)(_sampleData?.Length ?? 0),
            EndLoop = (uint)(_sampleData?.Length ?? 0)
        };
        terminal.SetName("EOS");
        WriteSampleHeader(ms, terminal);

        return ms.ToArray();
    }

    #region Writing Helpers

    private static void WriteChunkId(Stream stream, string id)
    {
        var bytes = Encoding.ASCII.GetBytes(id);
        stream.Write(bytes, 0, 4);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt16(Stream stream, short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteSubChunk(Stream stream, string id, Action<BinaryWriter> writeContent)
    {
        using var contentStream = new MemoryStream();
        using var writer = new BinaryWriter(contentStream);
        writeContent(writer);
        writer.Flush();

        var content = contentStream.ToArray();
        var paddedLength = content.Length;
        if (paddedLength % 2 != 0) paddedLength++;

        WriteChunkId(stream, id);
        WriteUInt32(stream, (uint)content.Length);
        stream.Write(content, 0, content.Length);
        if (content.Length % 2 != 0)
            stream.WriteByte(0);
    }

    private static void WriteStringChunk(Stream stream, string id, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
        var length = bytes.Length + 1; // Include null terminator
        if (length % 2 != 0) length++;

        WriteChunkId(stream, id);
        WriteUInt32(stream, (uint)length);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0); // Null terminator
        if ((bytes.Length + 1) % 2 != 0)
            stream.WriteByte(0); // Padding
    }

    private static void WriteRawChunk(Stream stream, string id, byte[] data)
    {
        WriteChunkId(stream, id);
        WriteUInt32(stream, (uint)data.Length);
        stream.Write(data, 0, data.Length);
        if (data.Length % 2 != 0)
            stream.WriteByte(0);
    }

    private static byte[] WrapListChunk(string type, byte[] content)
    {
        using var ms = new MemoryStream();
        WriteChunkId(ms, "LIST");
        WriteUInt32(ms, (uint)(4 + content.Length));
        WriteChunkId(ms, type);
        ms.Write(content, 0, content.Length);
        return ms.ToArray();
    }

    private static void WritePresetHeader(Stream stream, RawPresetHeader header)
    {
        stream.Write(header.Name ?? new byte[20], 0, 20);
        WriteUInt16(stream, header.Preset);
        WriteUInt16(stream, header.Bank);
        WriteUInt16(stream, header.PresetBagIndex);
        WriteUInt32(stream, header.Library);
        WriteUInt32(stream, header.Genre);
        WriteUInt32(stream, header.Morphology);
    }

    private static void WriteInstrumentHeader(Stream stream, RawInstrumentHeader header)
    {
        stream.Write(header.Name ?? new byte[20], 0, 20);
        WriteUInt16(stream, header.InstrumentBagIndex);
    }

    private static void WriteSampleHeader(Stream stream, RawSampleHeader header)
    {
        stream.Write(header.Name ?? new byte[20], 0, 20);
        WriteUInt32(stream, header.Start);
        WriteUInt32(stream, header.End);
        WriteUInt32(stream, header.StartLoop);
        WriteUInt32(stream, header.EndLoop);
        WriteUInt32(stream, header.SampleRate);
        stream.WriteByte(header.OriginalPitch);
        stream.WriteByte((byte)header.PitchCorrection);
        WriteUInt16(stream, header.SampleLink);
        WriteUInt16(stream, header.SampleType);
    }

    private static void WriteBag(Stream stream, ushort genIndex, ushort modIndex)
    {
        WriteUInt16(stream, genIndex);
        WriteUInt16(stream, modIndex);
    }

    private static void WriteGenerator(Stream stream, Generator gen)
    {
        WriteUInt16(stream, (ushort)gen.Type);
        WriteInt16(stream, gen.Amount.SignedAmount);
    }

    private static void WriteModulator(Stream stream, Modulator mod)
    {
        WriteUInt16(stream, mod.SourceOperator);
        WriteUInt16(stream, (ushort)mod.Destination);
        WriteInt16(stream, mod.Amount);
        WriteUInt16(stream, mod.AmountSourceOperator);
        WriteUInt16(stream, (ushort)mod.Transform);
    }

    #endregion

    /// <summary>
    /// Creates an Sf2Writer from an Sf2Reader (for modification).
    /// </summary>
    public static Sf2Writer FromReader(Sf2Reader reader)
    {
        var writer = new Sf2Writer
        {
            _info = reader.Info
        };

        // Copy sample data
        if (!reader.SampleData.IsEmpty)
        {
            writer._sampleData = reader.SampleData.ToArray();
        }

        // Copy sample headers
        foreach (var header in reader.SampleHeaders)
        {
            writer._sampleHeaders.Add(header);
        }

        // Copy instruments
        foreach (var inst in reader.Instruments)
        {
            writer._instruments.Add(inst);
        }

        // Copy presets
        foreach (var preset in reader.Presets)
        {
            writer._presets.Add(preset);
        }

        return writer;
    }
}
