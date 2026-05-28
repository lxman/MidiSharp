using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MidiSharp.Sf2;

/// <summary>
/// Reads and parses SoundFont 2.0 (SF2) files.
/// </summary>
public sealed class Sf2Reader
{
    private readonly List<Preset> _presets = [];
    private readonly List<Instrument> _instruments = [];
    private readonly List<SampleHeader> _sampleHeaders = [];
    private short[]? _sampleData;
    private readonly Sf2Info _info = new();
    private readonly List<Sf2Error> _errors = [];

    /// <summary>
    /// The presets in the SoundFont.
    /// </summary>
    public IReadOnlyList<Preset> Presets => _presets;

    /// <summary>
    /// The instruments in the SoundFont.
    /// </summary>
    public IReadOnlyList<Instrument> Instruments => _instruments;

    /// <summary>
    /// The sample headers in the SoundFont.
    /// </summary>
    public IReadOnlyList<SampleHeader> SampleHeaders => _sampleHeaders;

    /// <summary>
    /// The raw 16-bit PCM sample data.
    /// </summary>
    public ReadOnlySpan<short> SampleData => _sampleData;

    /// <summary>
    /// The SoundFont info/metadata.
    /// </summary>
    public Sf2Info Info => _info;

    /// <summary>
    /// Whether the file was parsed successfully.
    /// </summary>
    public bool IsValid => _errors.Count == 0 || _errors is [Sf2Error.Success];

    /// <summary>
    /// Any errors encountered during parsing.
    /// </summary>
    public IReadOnlyList<Sf2Error> Errors => _errors;

    /// <summary>
    /// Reads an SF2 file from a file path.
    /// </summary>
    public static Sf2Reader ReadFile(string path)
    {
        var data = File.ReadAllBytes(path);
        return Read(data);
    }

    /// <summary>
    /// Reads an SF2 file from a byte array.
    /// </summary>
    public static Sf2Reader Read(byte[] data)
    {
        return Read(data.AsSpan());
    }

    /// <summary>
    /// Reads an SF2 file from a span.
    /// </summary>
    public static Sf2Reader Read(ReadOnlySpan<byte> data)
    {
        var reader = new Sf2Reader();
        reader.Parse(data);
        return reader;
    }

    private void Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 12)
            {
                _errors.Add(Sf2Error.RiffChunkTooSmall);
                return;
            }

            // Read RIFF header
            var riffId = Encoding.ASCII.GetString(data.Slice(0, 4));
            if (riffId != "RIFF")
            {
                _errors.Add(Sf2Error.FileBroken);
                return;
            }

            var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
            var riffType = Encoding.ASCII.GetString(data.Slice(8, 4));

            if (riffType != "sfbk")
            {
                _errors.Add(Sf2Error.FileBroken);
                return;
            }

            if (riffSize + 8 > data.Length)
            {
                _errors.Add(Sf2Error.RiffChunkTooLarge);
                return;
            }

            // Parse the three LIST chunks: INFO, sdta, pdta
            var offset = 12;
            ReadOnlySpan<byte> infoChunk = default;
            ReadOnlySpan<byte> sdtaChunk = default;
            ReadOnlySpan<byte> pdtaChunk = default;

            while (offset < data.Length - 8)
            {
                var chunkId = Encoding.ASCII.GetString(data.Slice(offset, 4));
                var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4));

                if (chunkId == "LIST")
                {
                    var listType = Encoding.ASCII.GetString(data.Slice(offset + 8, 4));
                    var listData = data.Slice(offset + 12, (int)chunkSize - 4);

                    switch (listType)
                    {
                        case "INFO":
                            infoChunk = listData;
                            break;
                        case "sdta":
                            sdtaChunk = listData;
                            break;
                        case "pdta":
                            pdtaChunk = listData;
                            break;
                    }
                }

                offset += 8 + (int)chunkSize;
                if (chunkSize % 2 != 0) offset++; // Pad to even boundary
            }

            // Parse INFO chunk
            if (!infoChunk.IsEmpty)
            {
                ParseInfoChunk(infoChunk);
            }

            // Parse sdta (sample data) chunk
            if (!sdtaChunk.IsEmpty)
            {
                ParseSdtaChunk(sdtaChunk);
            }

            // Parse pdta (preset data) chunk
            if (!pdtaChunk.IsEmpty)
            {
                ParsePdtaChunk(pdtaChunk);
            }

            _errors.Add(Sf2Error.Success);
        }
        catch (Exception)
        {
            _errors.Add(Sf2Error.FileBroken);
        }
    }

    private void ParseInfoChunk(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length - 8)
        {
            var chunkId = Encoding.ASCII.GetString(data.Slice(offset, 4));
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4));
            var chunkData = data.Slice(offset + 8, (int)chunkSize);

            switch (chunkId)
            {
                case "ifil":
                    if (chunkSize >= 4)
                    {
                        _info.Version = new Sf2Version(
                            BinaryPrimitives.ReadUInt16LittleEndian(chunkData),
                            BinaryPrimitives.ReadUInt16LittleEndian(chunkData.Slice(2)));
                    }
                    break;
                case "isng":
                    _info.SoundEngine = GetNullTerminatedString(chunkData);
                    break;
                case "INAM":
                    _info.BankName = GetNullTerminatedString(chunkData);
                    break;
                case "irom":
                    _info.RomName = GetNullTerminatedString(chunkData);
                    break;
                case "iver":
                    if (chunkSize >= 4)
                    {
                        _info.RomVersion = new Sf2Version(
                            BinaryPrimitives.ReadUInt16LittleEndian(chunkData),
                            BinaryPrimitives.ReadUInt16LittleEndian(chunkData.Slice(2)));
                    }
                    break;
                case "ICRD":
                    _info.CreationDate = GetNullTerminatedString(chunkData);
                    break;
                case "IENG":
                    _info.Engineers = GetNullTerminatedString(chunkData);
                    break;
                case "IPRD":
                    _info.Product = GetNullTerminatedString(chunkData);
                    break;
                case "ICOP":
                    _info.Copyright = GetNullTerminatedString(chunkData);
                    break;
                case "ICMT":
                    _info.Comments = GetNullTerminatedString(chunkData);
                    break;
                case "ISFT":
                    _info.Tools = GetNullTerminatedString(chunkData);
                    break;
            }

            offset += 8 + (int)chunkSize;
            if (chunkSize % 2 != 0) offset++;
        }
    }

    private void ParseSdtaChunk(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length - 8)
        {
            var chunkId = Encoding.ASCII.GetString(data.Slice(offset, 4));
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4));
            var chunkData = data.Slice(offset + 8, (int)chunkSize);

            if (chunkId == "smpl")
            {
                // 16-bit PCM sample data
                var sampleCount = (int)chunkSize / 2;
                _sampleData = new short[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                {
                    _sampleData[i] = BinaryPrimitives.ReadInt16LittleEndian(chunkData.Slice(i * 2));
                }
            }
            // sm24 chunk for 24-bit samples could be handled here

            offset += 8 + (int)chunkSize;
            if (chunkSize % 2 != 0) offset++;
        }
    }

    private void ParsePdtaChunk(ReadOnlySpan<byte> data)
    {
        // Extract all sub-chunks
        ReadOnlySpan<byte> phdr = default, pbag = default, pmod = default, pgen = default;
        ReadOnlySpan<byte> inst = default, ibag = default, imod = default, igen = default;
        ReadOnlySpan<byte> shdr = default;

        var offset = 0;
        while (offset < data.Length - 8)
        {
            var chunkId = Encoding.ASCII.GetString(data.Slice(offset, 4));
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4));
            var chunkData = data.Slice(offset + 8, (int)chunkSize);

            switch (chunkId)
            {
                case "phdr": phdr = chunkData; break;
                case "pbag": pbag = chunkData; break;
                case "pmod": pmod = chunkData; break;
                case "pgen": pgen = chunkData; break;
                case "inst": inst = chunkData; break;
                case "ibag": ibag = chunkData; break;
                case "imod": imod = chunkData; break;
                case "igen": igen = chunkData; break;
                case "shdr": shdr = chunkData; break;
            }

            offset += 8 + (int)chunkSize;
            if (chunkSize % 2 != 0) offset++;
        }

        // Parse sample headers
        var sampleHeaders = ParseSampleHeaders(shdr);

        // Parse raw data
        var presetHeaders = ParsePresetHeaders(phdr);
        var presetBags = ParseBags(pbag);
        var presetGens = ParseGenerators(pgen);
        var presetMods = ParseModulators(pmod);

        var instrumentHeaders = ParseInstrumentHeaders(inst);
        var instrumentBags = ParseBags(ibag);
        var instrumentGens = ParseGenerators(igen);
        var instrumentMods = ParseModulators(imod);

        // Build instruments (excluding terminal)
        for (var i = 0; i < instrumentHeaders.Count - 1; i++)
        {
            var instHeader = instrumentHeaders[i];
            var instrument = Instrument.FromRawHeader(instHeader, i);

            int startBag = instHeader.InstrumentBagIndex;
            int endBag = instrumentHeaders[i + 1].InstrumentBagIndex;

            for (var b = startBag; b < endBag; b++)
            {
                var zone = new Zone { Index = b - startBag };

                int startGen = instrumentBags[b].GenIndex;
                int endGen = instrumentBags[b + 1].GenIndex;
                int startMod = instrumentBags[b].ModIndex;
                int endMod = instrumentBags[b + 1].ModIndex;

                for (var g = startGen; g < endGen; g++)
                {
                    var gen = Generator.FromRaw(instrumentGens[g]);
                    zone.AddGenerator(gen);
                }

                for (var m = startMod; m < endMod; m++)
                {
                    var mod = Modulator.FromRaw(instrumentMods[m]);
                    zone.AddModulator(mod);
                }

                // Check for sampleID generator (must be last)
                if (zone.Generators.Count > 0)
                {
                    var lastGen = zone.Generators[^1];
                    if (lastGen.Type == GeneratorType.SampleId)
                    {
                        int sampleIndex = lastGen.UnsignedValue;
                        if (sampleIndex < sampleHeaders.Count)
                        {
                            zone.Sample = sampleHeaders[sampleIndex];
                        }
                        zone.RemoveLastGenerator();
                    }
                }

                instrument.AddZone(zone);
            }

            _instruments.Add(instrument);
        }

        // Build presets (excluding terminal)
        for (var i = 0; i < presetHeaders.Count - 1; i++)
        {
            var phdrRaw = presetHeaders[i];
            var preset = Preset.FromRawHeader(phdrRaw);

            int startBag = phdrRaw.PresetBagIndex;
            int endBag = presetHeaders[i + 1].PresetBagIndex;

            for (var b = startBag; b < endBag; b++)
            {
                var zone = new Zone { Index = b - startBag };

                int startGen = presetBags[b].GenIndex;
                int endGen = presetBags[b + 1].GenIndex;
                int startMod = presetBags[b].ModIndex;
                int endMod = presetBags[b + 1].ModIndex;

                for (var g = startGen; g < endGen; g++)
                {
                    var gen = Generator.FromRaw(presetGens[g]);
                    zone.AddGenerator(gen);
                }

                for (var m = startMod; m < endMod; m++)
                {
                    var mod = Modulator.FromRaw(presetMods[m]);
                    zone.AddModulator(mod);
                }

                // Check for instrument generator (must be last)
                if (zone.Generators.Count > 0)
                {
                    var lastGen = zone.Generators[^1];
                    if (lastGen.Type == GeneratorType.Instrument)
                    {
                        zone.InstrumentIndex = lastGen.UnsignedValue;
                        zone.RemoveLastGenerator();
                    }
                }

                preset.AddZone(zone);
            }

            _presets.Add(preset);
        }

        // Store sample headers
        foreach (var sh in sampleHeaders)
        {
            if (sh.Name != "EOS") // Skip terminal
                _sampleHeaders.Add(sh);
        }
    }

    private List<RawPresetHeader> ParsePresetHeaders(ReadOnlySpan<byte> data)
    {
        var list = new List<RawPresetHeader>();
        var count = data.Length / RawPresetHeader.Size;

        for (var i = 0; i < count; i++)
        {
            var slice = data.Slice(i * RawPresetHeader.Size, RawPresetHeader.Size);
            var header = new RawPresetHeader
            {
                Name = slice.Slice(0, 20).ToArray(),
                Preset = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(20)),
                Bank = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(22)),
                PresetBagIndex = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(24)),
                Library = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(26)),
                Genre = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(30)),
                Morphology = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(34))
            };

            list.Add(header);
        }

        return list;
    }

    private List<RawInstrumentHeader> ParseInstrumentHeaders(ReadOnlySpan<byte> data)
    {
        var list = new List<RawInstrumentHeader>();
        var count = data.Length / RawInstrumentHeader.Size;

        for (var i = 0; i < count; i++)
        {
            var slice = data.Slice(i * RawInstrumentHeader.Size, RawInstrumentHeader.Size);
            var header = new RawInstrumentHeader
            {
                Name = slice.Slice(0, 20).ToArray(),
                InstrumentBagIndex = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(20))
            };

            list.Add(header);
        }

        return list;
    }

    private List<SampleHeader> ParseSampleHeaders(ReadOnlySpan<byte> data)
    {
        var list = new List<SampleHeader>();
        var count = data.Length / RawSampleHeader.Size;

        for (var i = 0; i < count; i++)
        {
            var slice = data.Slice(i * RawSampleHeader.Size, RawSampleHeader.Size);
            var raw = ParseRawSampleHeader(slice);
            list.Add(new SampleHeader(raw, i));
        }

        return list;
    }

    private RawSampleHeader ParseRawSampleHeader(ReadOnlySpan<byte> slice)
    {
        return new RawSampleHeader
        {
            Name = slice.Slice(0, 20).ToArray(),
            Start = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(20)),
            End = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(24)),
            StartLoop = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(28)),
            EndLoop = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(32)),
            SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(36)),
            OriginalPitch = slice[40],
            PitchCorrection = (sbyte)slice[41],
            SampleLink = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(42)),
            SampleType = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(44))
        };
    }

    private List<RawBag> ParseBags(ReadOnlySpan<byte> data)
    {
        var list = new List<RawBag>();
        var count = data.Length / RawBag.Size;

        for (var i = 0; i < count; i++)
        {
            var slice = data.Slice(i * RawBag.Size, RawBag.Size);
            list.Add(new RawBag
            {
                GenIndex = BinaryPrimitives.ReadUInt16LittleEndian(slice),
                ModIndex = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(2))
            });
        }

        return list;
    }

    private List<RawGenerator> ParseGenerators(ReadOnlySpan<byte> data)
    {
        var list = new List<RawGenerator>();
        var count = data.Length / RawGenerator.Size;

        for (var i = 0; i < count; i++)
        {
            var slice = data.Slice(i * RawGenerator.Size, RawGenerator.Size);
            var gen = new RawGenerator
            {
                Operator = BinaryPrimitives.ReadUInt16LittleEndian(slice)
            };
            gen.Amount.SignedAmount = BinaryPrimitives.ReadInt16LittleEndian(slice.Slice(2));
            list.Add(gen);
        }

        return list;
    }

    private List<RawModulator> ParseModulators(ReadOnlySpan<byte> data)
    {
        var list = new List<RawModulator>();
        var count = data.Length / RawModulator.Size;

        for (var i = 0; i < count; i++)
        {
            var slice = data.Slice(i * RawModulator.Size, RawModulator.Size);
            list.Add(new RawModulator
            {
                SourceOperator = BinaryPrimitives.ReadUInt16LittleEndian(slice),
                DestOperator = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(2)),
                Amount = BinaryPrimitives.ReadInt16LittleEndian(slice.Slice(4)),
                AmountSourceOperator = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(6)),
                TransformOperator = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(8))
            });
        }

        return list;
    }

    private static string GetNullTerminatedString(ReadOnlySpan<byte> data)
    {
        var length = data.IndexOf((byte)0);
        if (length < 0) length = data.Length;
        return Encoding.ASCII.GetString(data.Slice(0, length));
    }

    /// <summary>
    /// Gets a preset by bank and program number.
    /// </summary>
    public Preset? GetPreset(ushort bank, ushort program)
    {
        foreach (var preset in _presets)
        {
            if (preset.Bank == bank && preset.PresetNumber == program)
                return preset;
        }
        return null;
    }

    /// <summary>
    /// Gets the instrument referenced by a preset zone.
    /// </summary>
    public Instrument? GetInstrument(Zone presetZone)
    {
        if (presetZone.InstrumentIndex >= 0 && presetZone.InstrumentIndex < _instruments.Count)
            return _instruments[presetZone.InstrumentIndex];
        return null;
    }

    /// <summary>
    /// Extracts sample data for a sample header.
    /// </summary>
    public short[]? GetSampleData(SampleHeader header)
    {
        if (_sampleData == null) return null;
        if (header.Start >= _sampleData.Length || header.End > _sampleData.Length)
            return null;

        var length = (int)(header.End - header.Start);
        var data = new short[length];
        Array.Copy(_sampleData, header.Start, data, 0, length);
        return data;
    }

    /// <summary>
    /// Gets a list of all banks in the SoundFont.
    /// </summary>
    public List<ushort> GetBanks()
    {
        var banks = new HashSet<ushort>();
        foreach (var preset in _presets)
            banks.Add(preset.Bank);

        var list = new List<ushort>(banks);
        list.Sort();
        return list;
    }

    /// <summary>
    /// Gets all presets in a specific bank.
    /// </summary>
    public List<Preset> GetPresetsInBank(ushort bank)
    {
        var list = new List<Preset>();
        foreach (var preset in _presets)
        {
            if (preset.Bank == bank)
                list.Add(preset);
        }
        list.Sort((a, b) => a.PresetNumber.CompareTo(b.PresetNumber));
        return list;
    }
}

/// <summary>
/// SF2 file metadata.
/// </summary>
public sealed class Sf2Info
{
    public Sf2Version Version { get; set; }
    public string SoundEngine { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string RomName { get; set; } = string.Empty;
    public Sf2Version RomVersion { get; set; }
    public string CreationDate { get; set; } = string.Empty;
    public string Engineers { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Copyright { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string Tools { get; set; } = string.Empty;
}
