using System.Text;

namespace SF2Net.SmokeTest;

internal static class InspectBag
{
    public static void Dump(string path, string tag)
    {
        var data = File.ReadAllBytes(path);
        var idx = IndexOf(data, tag);
        if (idx < 0) { Console.WriteLine($"no {tag} tag"); return; }
        var size = BitConverter.ToUInt32(data, idx + 4);
        var count = (int)(size / 4);
        Console.WriteLine($"{tag} chunk at {idx}, size {size}, count {count}");
        ushort prevGen = 0, prevMod = 0;
        var violations = 0;
        for (var i = 0; i < count; i++)
        {
            var o = idx + 8 + i * 4;
            var gen = BitConverter.ToUInt16(data, o);
            var mod = BitConverter.ToUInt16(data, o + 2);
            var mark = "";
            if (i > 0)
            {
                if (gen < prevGen) { mark += $"  GEN DECREASED ({prevGen}->{gen})"; violations++; }
                if (mod < prevMod) { mark += $"  MOD DECREASED ({prevMod}->{mod})"; violations++; }
            }
            if (!string.IsNullOrEmpty(mark) || i < 3 || i >= count - 3)
                Console.WriteLine($"  [{i,4}] gen={gen,6} mod={mod,6}{mark}");
            prevGen = gen; prevMod = mod;
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
