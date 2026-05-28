using System.Runtime.InteropServices;

namespace SF2.Net;

/// <summary>
/// The 16-bit amount payload of a generator. Interpretation depends on the
/// generator operator: unsigned word, signed short, or a pair of bytes (range).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 2)]
public readonly struct GeneratorAmount : IEquatable<GeneratorAmount>
{
    [FieldOffset(0)] private readonly ushort _word;
    [FieldOffset(0)] private readonly short _signed;
    [FieldOffset(0)] private readonly byte _low;
    [FieldOffset(1)] private readonly byte _high;

    public GeneratorAmount(ushort word) : this()
    {
        _word = word;
    }

    public GeneratorAmount(short signed) : this()
    {
        _signed = signed;
    }

    public GeneratorAmount(byte low, byte high) : this()
    {
        _low = low;
        _high = high;
    }

    public ushort Word => _word;
    public short Signed => _signed;
    public ByteRange Range => new(_low, _high);

    public bool Equals(GeneratorAmount other) => _word == other._word;
    public override bool Equals(object? obj) => obj is GeneratorAmount g && Equals(g);
    public override int GetHashCode() => _word.GetHashCode();
    public static bool operator ==(GeneratorAmount a, GeneratorAmount b) => a.Equals(b);
    public static bool operator !=(GeneratorAmount a, GeneratorAmount b) => !a.Equals(b);

    public override string ToString() => $"0x{_word:X4}";
}
