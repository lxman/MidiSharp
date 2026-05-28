namespace SF2.Net;

public readonly record struct VersionTag(ushort Major, ushort Minor)
{
    public override string ToString() => $"{Major}.{Minor:D2}";
}
