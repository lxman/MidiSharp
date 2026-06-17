using System.Buffers.Binary;
using System.Text;

namespace SF2.Net.Io;

internal static class BinaryHelpers
{
    /// <summary>SoundFont 2 file content is ASCII for tags and names.</summary>
    public static readonly Encoding Ascii = Encoding.ASCII;

    public static ushort ReadUInt16LE(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    public static short ReadInt16LE(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));

    public static uint ReadUInt32LE(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    public static void WriteUInt16LE(Span<byte> dest, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(offset, 2), value);

    public static void WriteInt16LE(Span<byte> dest, int offset, short value)
        => BinaryPrimitives.WriteInt16LittleEndian(dest.Slice(offset, 2), value);

    public static void WriteUInt32LE(Span<byte> dest, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(offset, 4), value);

    /// <summary>Reads a 4-byte ASCII tag (RIFF/sfbk/LIST/INFO/sdta/pdta/phdr/...).</summary>
    public static string ReadTag(ReadOnlySpan<byte> data, int offset)
        => Ascii.GetString(data.Slice(offset, 4).ToArray());

    /// <summary>Reads a fixed-width zero-padded ASCII string and trims at the first NUL or trailing whitespace.</summary>
    public static string ReadFixedAscii(ReadOnlySpan<byte> data, int offset, int length)
    {
        var slice = data.Slice(offset, length);
        var nul = slice.IndexOf((byte)0);
        if (nul >= 0) slice = slice.Slice(0, nul);
        return Ascii.GetString(slice.ToArray()).TrimEnd();
    }

    /// <summary>
    /// Writes a fixed-width ASCII string, zero-padded if shorter than <paramref name="length"/>,
    /// truncated (and zero-terminated) if longer. Always writes exactly <paramref name="length"/> bytes.
    /// </summary>
    public static void WriteFixedAscii(Span<byte> dest, int offset, int length, string value)
    {
        var region = dest.Slice(offset, length);
        region.Clear();
        var n = Math.Min(value.Length, length);
        for (var i = 0; i < n; i++)
        {
            var c = value[i];
            region[i] = c < 128 ? (byte)c : (byte)'?';
        }
        // Force a NUL only when the value was strictly longer than the field and was truncated.
        if (value.Length > length) region[length - 1] = 0;
    }
}
