using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MidiSharp.Sf2;

/// <summary>
/// Represents a SoundFont 2.0 file with read, query, modify, and write capabilities.
/// This is the main public API for working with SF2 files.
/// </summary>
public sealed class SoundFont
{
    private readonly List<Preset> _presets = [];
    private readonly List<Instrument> _instruments = [];
    private readonly List<SampleHeader> _sampleHeaders = [];
    private short[]? _sampleData;
    private Sf2Info _info = new();
    private readonly List<Sf2Error> _errors = [];

    /// <summary>
    /// Creates a new empty SoundFont.
    /// </summary>
    public SoundFont()
    {
        _info.Version = new Sf2Version(2, 1);
        _info.SoundEngine = "EMU8000";
        _info.BankName = "Untitled";
    }

    /// <summary>
    /// Gets or sets the SoundFont info/metadata.
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
    /// Gets the sample headers in the SoundFont.
    /// </summary>
    public IReadOnlyList<SampleHeader> SampleHeaders => _sampleHeaders;

    /// <summary>
    /// Gets the raw 16-bit PCM sample data.
    /// </summary>
    public ReadOnlySpan<short> SampleData => _sampleData;

    /// <summary>
    /// Whether the file was loaded/parsed successfully.
    /// </summary>
    public bool IsValid => _errors.Count == 0 || _errors is [Sf2Error.Success];

    /// <summary>
    /// Any errors encountered during loading.
    /// </summary>
    public IReadOnlyList<Sf2Error> Errors => _errors;

    #region Loading

    /// <summary>
    /// Loads a SoundFont from a file.
    /// </summary>
    public static SoundFont Load(string path)
    {
        var reader = Sf2Reader.ReadFile(path);
        return FromReader(reader);
    }

    /// <summary>
    /// Loads a SoundFont from a byte array.
    /// </summary>
    public static SoundFont Load(byte[] data)
    {
        var reader = Sf2Reader.Read(data);
        return FromReader(reader);
    }

    /// <summary>
    /// Loads a SoundFont from a span.
    /// </summary>
    public static SoundFont Load(ReadOnlySpan<byte> data)
    {
        var reader = Sf2Reader.Read(data);
        return FromReader(reader);
    }

    private static SoundFont FromReader(Sf2Reader reader)
    {
        var sf = new SoundFont
        {
            _info = reader.Info
        };

        foreach (var e in reader.Errors)
            sf._errors.Add(e);

        if (!reader.SampleData.IsEmpty)
            sf._sampleData = reader.SampleData.ToArray();

        foreach (var h in reader.SampleHeaders)
            sf._sampleHeaders.Add(h);

        foreach (var i in reader.Instruments)
            sf._instruments.Add(i);

        foreach (var p in reader.Presets)
            sf._presets.Add(p);

        return sf;
    }

    #endregion

    #region Saving

    /// <summary>
    /// Saves the SoundFont to a file.
    /// </summary>
    public void Save(string path)
    {
        var data = ToBytes();
        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// Converts the SoundFont to a byte array.
    /// </summary>
    public byte[] ToBytes()
    {
        var writer = ToWriter();
        return writer.Write();
    }

    /// <summary>
    /// Writes the SoundFont to a stream.
    /// </summary>
    public void Save(Stream stream)
    {
        var writer = ToWriter();
        writer.Write(stream);
    }

    private Sf2Writer ToWriter()
    {
        var writer = new Sf2Writer { Info = _info };

        if (_sampleData != null)
            writer.SetSampleData(_sampleData);

        foreach (var h in _sampleHeaders)
            writer.AddSample(h, []); // Headers already have offsets set

        foreach (var i in _instruments)
            writer.AddInstrument(i);

        foreach (var p in _presets)
            writer.AddPreset(p);

        return writer;
    }

    #endregion

    #region Query Methods

    /// <summary>
    /// Gets a list of all bank numbers in the SoundFont.
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
        return _presets
            .Where(p => p.Bank == bank)
            .OrderBy(p => p.PresetNumber)
            .ToList();
    }

    /// <summary>
    /// Gets a preset by bank and program number.
    /// </summary>
    public Preset? GetPreset(ushort bank, ushort program)
    {
        return _presets.FirstOrDefault(p => p.Bank == bank && p.PresetNumber == program);
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
    /// Gets the instrument by index.
    /// </summary>
    public Instrument? GetInstrument(int index)
    {
        if (index >= 0 && index < _instruments.Count)
            return _instruments[index];
        return null;
    }

    /// <summary>
    /// Gets the sample header by index.
    /// </summary>
    public SampleHeader? GetSampleHeader(int index)
    {
        if (index >= 0 && index < _sampleHeaders.Count)
            return _sampleHeaders[index];
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
    /// Gets the number of zones in a preset.
    /// </summary>
    public int GetPresetZoneCount(ushort bank, ushort preset)
    {
        var p = GetPreset(bank, preset);
        return p?.Zones.Count ?? 0;
    }

    /// <summary>
    /// Checks if a preset zone references an instrument.
    /// </summary>
    public bool HasInstrument(ushort bank, ushort preset, int zoneIndex)
    {
        var p = GetPreset(bank, preset);
        if (p == null || zoneIndex < 0 || zoneIndex >= p.Zones.Count)
            return false;
        return p.Zones[zoneIndex].InstrumentIndex >= 0;
    }

    /// <summary>
    /// Gets the instrument name for a preset zone.
    /// </summary>
    public string? GetZoneInstrumentName(ushort bank, ushort preset, int zoneIndex)
    {
        var p = GetPreset(bank, preset);
        if (p == null || zoneIndex < 0 || zoneIndex >= p.Zones.Count)
            return null;

        var inst = GetInstrument(p.Zones[zoneIndex]);
        return inst?.Name;
    }

    /// <summary>
    /// Gets all instrument names in the SoundFont.
    /// </summary>
    public List<string> GetInstrumentNames()
    {
        return _instruments.Select(i => i.Name).ToList();
    }

    /// <summary>
    /// Gets the generators for a preset zone.
    /// </summary>
    public IReadOnlyList<Generator> GetPresetGenerators(ushort bank, ushort preset, int zoneIndex)
    {
        var p = GetPreset(bank, preset);
        if (p == null || zoneIndex < 0 || zoneIndex >= p.Zones.Count)
            return [];
        return p.Zones[zoneIndex].Generators;
    }

    /// <summary>
    /// Gets the modulators for a preset zone.
    /// </summary>
    public IReadOnlyList<Modulator> GetPresetModulators(ushort bank, ushort preset, int zoneIndex)
    {
        var p = GetPreset(bank, preset);
        if (p == null || zoneIndex < 0 || zoneIndex >= p.Zones.Count)
            return [];
        return p.Zones[zoneIndex].Modulators;
    }

    /// <summary>
    /// Gets the generators for an instrument zone.
    /// </summary>
    public IReadOnlyList<Generator> GetInstrumentGenerators(ushort bank, ushort preset, int presetZone, int instrumentZone)
    {
        var p = GetPreset(bank, preset);
        if (p == null || presetZone < 0 || presetZone >= p.Zones.Count)
            return [];

        var inst = GetInstrument(p.Zones[presetZone]);
        if (inst == null || instrumentZone < 0 || instrumentZone >= inst.Zones.Count)
            return [];

        return inst.Zones[instrumentZone].Generators;
    }

    /// <summary>
    /// Gets the modulators for an instrument zone.
    /// </summary>
    public IReadOnlyList<Modulator> GetInstrumentModulators(ushort bank, ushort preset, int presetZone, int instrumentZone)
    {
        var p = GetPreset(bank, preset);
        if (p == null || presetZone < 0 || presetZone >= p.Zones.Count)
            return [];

        var inst = GetInstrument(p.Zones[presetZone]);
        if (inst == null || instrumentZone < 0 || instrumentZone >= inst.Zones.Count)
            return [];

        return inst.Zones[instrumentZone].Modulators;
    }

    /// <summary>
    /// Checks if an instrument zone has a sample.
    /// </summary>
    public bool HasSample(ushort bank, ushort preset, int presetZone, int instrumentZone)
    {
        var p = GetPreset(bank, preset);
        if (p == null || presetZone < 0 || presetZone >= p.Zones.Count)
            return false;

        var inst = GetInstrument(p.Zones[presetZone]);
        if (inst == null || instrumentZone < 0 || instrumentZone >= inst.Zones.Count)
            return false;

        return inst.Zones[instrumentZone].Sample != null;
    }

    /// <summary>
    /// Gets the sample header for an instrument zone.
    /// </summary>
    public SampleHeader? GetSampleHeader(ushort bank, ushort preset, int presetZone, int instrumentZone)
    {
        var p = GetPreset(bank, preset);
        if (p == null || presetZone < 0 || presetZone >= p.Zones.Count)
            return null;

        var inst = GetInstrument(p.Zones[presetZone]);
        if (inst == null || instrumentZone < 0 || instrumentZone >= inst.Zones.Count)
            return null;

        return inst.Zones[instrumentZone].Sample;
    }

    #endregion

    #region Modification Methods

    /// <summary>
    /// Adds a preset to the SoundFont.
    /// </summary>
    public void AddPreset(Preset preset)
    {
        _presets.Add(preset);
    }

    /// <summary>
    /// Removes a preset from the SoundFont.
    /// </summary>
    public bool RemovePreset(Preset preset)
    {
        return _presets.Remove(preset);
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
    /// Removes an instrument from the SoundFont.
    /// </summary>
    public bool RemoveInstrument(Instrument instrument)
    {
        return _instruments.Remove(instrument);
    }

    /// <summary>
    /// Adds a sample with its audio data.
    /// </summary>
    public void AddSample(SampleHeader header, short[] data)
    {
        header.Index = _sampleHeaders.Count;

        if (_sampleData == null)
        {
            header.Start = 0;
            header.End = (uint)data.Length;
            _sampleData = data;
        }
        else
        {
            var startOffset = (uint)_sampleData.Length;
            header.Start = startOffset;
            header.End = startOffset + (uint)data.Length;
            header.StartLoop = startOffset + (header.StartLoop - header.Start);
            header.EndLoop = startOffset + (header.EndLoop - header.Start);

            var newData = new short[_sampleData.Length + data.Length];
            Array.Copy(_sampleData, newData, _sampleData.Length);
            Array.Copy(data, 0, newData, _sampleData.Length, data.Length);
            _sampleData = newData;
        }

        _sampleHeaders.Add(header);
    }

    #endregion

    #region Extract/Export

    /// <summary>
    /// Extracts a single preset to a new SoundFont.
    /// </summary>
    public SoundFont ExtractPreset(ushort bank, ushort program)
    {
        var preset = GetPreset(bank, program);
        if (preset == null)
            throw new ArgumentException($"Preset {bank}:{program} not found");

        var extracted = new SoundFont();
        extracted.Info = new Sf2Info
        {
            Version = _info.Version,
            SoundEngine = _info.SoundEngine,
            BankName = preset.Name,
            CreationDate = DateTime.Now.ToString("yyyy-MM-dd"),
            Tools = "MidiSharp.Sf2"
        };

        // Track which instruments and samples are needed
        var usedInstruments = new HashSet<int>();
        var usedSamples = new HashSet<int>();

        // Find all instruments used by this preset
        foreach (var zone in preset.Zones)
        {
            if (zone.InstrumentIndex >= 0)
            {
                usedInstruments.Add(zone.InstrumentIndex);
                var inst = GetInstrument(zone.InstrumentIndex);
                if (inst != null)
                {
                    foreach (var izone in inst.Zones)
                    {
                        if (izone.Sample != null)
                            usedSamples.Add(izone.Sample.Index);
                    }
                }
            }
        }

        // Build mapping from old indices to new
        var sampleMapping = new Dictionary<int, int>();
        var instrumentMapping = new Dictionary<int, int>();

        // Copy samples
        foreach (var oldIndex in usedSamples.OrderBy(x => x))
        {
            var oldHeader = _sampleHeaders[oldIndex];
            var data = GetSampleData(oldHeader);
            if (data != null)
            {
                var newHeader = new SampleHeader
                {
                    Name = oldHeader.Name,
                    SampleRate = oldHeader.SampleRate,
                    OriginalPitch = oldHeader.OriginalPitch,
                    PitchCorrection = oldHeader.PitchCorrection,
                    SampleType = oldHeader.SampleType,
                    StartLoop = oldHeader.StartLoop - oldHeader.Start,
                    EndLoop = oldHeader.EndLoop - oldHeader.Start
                };
                sampleMapping[oldIndex] = extracted._sampleHeaders.Count;
                extracted.AddSample(newHeader, data);
            }
        }

        // Copy instruments
        foreach (var oldIndex in usedInstruments.OrderBy(x => x))
        {
            var oldInst = _instruments[oldIndex];
            var newInst = new Instrument { Name = oldInst.Name };

            foreach (var oldZone in oldInst.Zones)
            {
                var newZone = new Zone();

                foreach (var gen in oldZone.Generators)
                    newZone.AddGenerator(new Generator(gen.Type, gen.Amount));

                foreach (var mod in oldZone.Modulators)
                    newZone.AddModulator(new Modulator
                    {
                        SourceOperator = mod.SourceOperator,
                        Destination = mod.Destination,
                        Amount = mod.Amount,
                        AmountSourceOperator = mod.AmountSourceOperator,
                        Transform = mod.Transform
                    });

                if (oldZone.Sample != null && sampleMapping.TryGetValue(oldZone.Sample.Index, out var newSampleIndex))
                {
                    newZone.Sample = extracted._sampleHeaders[newSampleIndex];
                }

                newInst.AddZone(newZone);
            }

            instrumentMapping[oldIndex] = extracted._instruments.Count;
            extracted.AddInstrument(newInst);
        }

        // Copy preset
        var newPreset = new Preset
        {
            Name = preset.Name,
            PresetNumber = preset.PresetNumber,
            Bank = preset.Bank
        };

        foreach (var oldZone in preset.Zones)
        {
            var newZone = new Zone();

            foreach (var gen in oldZone.Generators)
                newZone.AddGenerator(new Generator(gen.Type, gen.Amount));

            foreach (var mod in oldZone.Modulators)
                newZone.AddModulator(new Modulator
                {
                    SourceOperator = mod.SourceOperator,
                    Destination = mod.Destination,
                    Amount = mod.Amount,
                    AmountSourceOperator = mod.AmountSourceOperator,
                    Transform = mod.Transform
                });

            if (oldZone.InstrumentIndex >= 0 && instrumentMapping.TryGetValue(oldZone.InstrumentIndex, out var newInstIndex))
            {
                newZone.InstrumentIndex = newInstIndex;
            }

            newPreset.AddZone(newZone);
        }

        extracted.AddPreset(newPreset);
        return extracted;
    }

    /// <summary>
    /// Exports sample data to a WAV file.
    /// </summary>
    public void ExportSampleToWav(SampleHeader header, string path)
    {
        var data = GetSampleData(header);
        if (data == null)
            throw new InvalidOperationException("Sample data not found");

        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);

        // WAV header
        var dataSize = data.Length * 2;
        var fileSize = 36 + dataSize;

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(fileSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // fmt chunk size
        bw.Write((short)1); // PCM format
        bw.Write((short)1); // Mono
        bw.Write((int)header.SampleRate);
        bw.Write((int)(header.SampleRate * 2)); // Byte rate
        bw.Write((short)2); // Block align
        bw.Write((short)16); // Bits per sample
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);

        foreach (var sample in data)
            bw.Write(sample);
    }

    #endregion

    public override string ToString()
    {
        return $"{_info.BankName} ({_presets.Count} presets, {_instruments.Count} instruments, {_sampleHeaders.Count} samples)";
    }
}
