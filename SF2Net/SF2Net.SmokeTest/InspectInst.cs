using System.Text;

namespace SF2Net.SmokeTest;

internal static class InspectInst
{
    public static void Dump(string path)
    {
        var data = File.ReadAllBytes(path);
        // Find inst chunk
        var idx = IndexOf(data, "inst");
        if (idx < 0) { Console.WriteLine("no inst tag"); return; }
        var size = BitConverter.ToUInt32(data, idx + 4);
        Console.WriteLine($"inst chunk at {idx}, size {size}, count {size/22}");
        ushort prev = 0;
        var violations = 0;
        for (var i = 0; i < size / 22; i++)
        {
            var o = idx + 8 + i * 22;
            var name = Encoding.ASCII.GetString(data, o, 20).TrimEnd('\0').TrimEnd();
            var bagIdx = BitConverter.ToUInt16(data, o + 20);
            var mark = "";
            if (i > 0)
            {
                if (bagIdx == prev) { mark = "  <-- equal (zero-zone instrument)"; violations++; }
                else if (bagIdx < prev) { mark = "  <-- DECREASING"; violations++; }
            }
            if (!string.IsNullOrEmpty(mark) || i < 5 || i >= size/22 - 5)
                Console.WriteLine($"  [{i,4}] bagIdx={bagIdx,6} name='{name}'{mark}");
            prev = bagIdx;
        }
        Console.WriteLine($"Total violations of strict-increase rule: {violations}");
    }

    private static int IndexOf(byte[] data, string tag)
    {
        var t = Encoding.ASCII.GetBytes(tag);
        for (var i = 0; i < data.Length - 4; i++)
        {
            if (data[i] == t[0] && data[i + 1] == t[1] && data[i + 2] == t[2] && data[i + 3] == t[3])
                return i;
        }
        return -1;
    }
}
