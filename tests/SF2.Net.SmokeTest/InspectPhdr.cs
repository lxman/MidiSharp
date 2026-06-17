using System.Text;

namespace SF2.Net.SmokeTest;

internal static class InspectPhdr
{
    public static void Dump(string path)
    {
        var data = File.ReadAllBytes(path);
        var idx = IndexOf(data, "phdr");
        if (idx < 0) { Console.WriteLine("no phdr tag"); return; }
        var size = BitConverter.ToUInt32(data, idx + 4);
        var count = (int)(size / 38);
        Console.WriteLine($"phdr at {idx}, size {size}, count {count}");
        ushort prev = 0;
        var violations = 0;
        for (var i = 0; i < count; i++)
        {
            var o = idx + 8 + i * 38;
            var name = Encoding.ASCII.GetString(data, o, 20).TrimEnd('\0').TrimEnd();
            var preset = BitConverter.ToUInt16(data, o + 20);
            var bank = BitConverter.ToUInt16(data, o + 22);
            var bagIdx = BitConverter.ToUInt16(data, o + 24);
            var mark = "";
            if (i > 0)
            {
                if (bagIdx == prev) { mark = "  EQUAL (zero-zone preset)"; violations++; }
                else if (bagIdx < prev) { mark = "  DECREASING"; violations++; }
            }
            if (!string.IsNullOrEmpty(mark) || i < 3 || i >= count - 3)
                Console.WriteLine($"  [{i,4}] bank={bank} preset={preset} bagIdx={bagIdx} name='{name}'{mark}");
            prev = bagIdx;
        }
        Console.WriteLine($"Total violations: {violations}");
    }

    private static int IndexOf(byte[] data, string tag)
    {
        var t = Encoding.ASCII.GetBytes(tag);
        for (var i = 0; i < data.Length - 4; i++)
            if (data[i] == t[0] && data[i + 1] == t[1] && data[i + 2] == t[2] && data[i + 3] == t[3])
                return i;
        return -1;
    }
}
