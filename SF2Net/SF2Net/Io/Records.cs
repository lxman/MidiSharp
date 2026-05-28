namespace SF2Net.Io;

/// <summary>Raw <c>phdr</c> record (38 bytes).</summary>
internal sealed class PresetHeaderRecord
{
    public string Name = string.Empty;
    public ushort Preset;
    public ushort Bank;
    public ushort BagIndex;
    public uint Library;
    public uint Genre;
    public uint Morphology;
}

/// <summary>Raw <c>pbag</c> or <c>ibag</c> record (4 bytes).</summary>
internal struct BagRecord
{
    public ushort GenIndex;
    public ushort ModIndex;
}

/// <summary>Raw <c>inst</c> record (22 bytes).</summary>
internal sealed class InstrumentRecord
{
    public string Name = string.Empty;
    public ushort BagIndex;
}
