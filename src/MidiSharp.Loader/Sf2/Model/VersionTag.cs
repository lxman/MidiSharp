namespace MidiSharp.Loader.Sf2.Model;

public readonly record struct VersionTag(ushort Major, ushort Minor)
{
    public override string ToString() => $"{Major}.{Minor:D2}";
}
