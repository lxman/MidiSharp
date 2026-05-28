namespace SF2Net;

/// <summary>
/// A byte range used by key-range and velocity-range generators.
/// </summary>
public readonly record struct ByteRange(byte Low, byte High)
{
    public override string ToString() => $"{Low}-{High}";
}
