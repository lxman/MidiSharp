using System.Text;

namespace SF2.Net.SmokeTest;

internal static class Inspect
{
    public static void DumpChunks(string path, int maxChunks = 50)
    {
        var data = File.ReadAllBytes(path);
        Console.WriteLine($"File: {path}  ({data.Length:N0} bytes)");
        if (data.Length < 12) { Console.WriteLine("  TOO SMALL"); return; }

        Console.WriteLine($"  RIFF tag='{Encoding.ASCII.GetString(data, 0, 4)}' size={BitConverter.ToUInt32(data, 4):N0} form='{Encoding.ASCII.GetString(data, 8, 4)}'");

        var pos = 12;
        for (var c = 0; c < maxChunks && pos < data.Length - 8; c++)
        {
            var tag = Encoding.ASCII.GetString(data, pos, 4);
            var size = BitConverter.ToUInt32(data, pos + 4);
            var form = (tag == "LIST" && pos + 12 <= data.Length) ? Encoding.ASCII.GetString(data, pos + 8, 4) : null;
            var note = "";
            if (pos + 8 + size > data.Length) note = "  *** SIZE EXCEEDS FILE ***";
            Console.WriteLine($"  @{pos,10:N0}  tag='{tag}'  size={size,12:N0}  {(form != null ? $"form='{form}'" : "")}{note}");

            // For LIST chunks, walk the inner sub-chunks
            if (tag == "LIST" && size > 4)
            {
                var subPos = pos + 12;
                var subEnd = pos + 8 + (int)size;
                while (subPos + 8 <= Math.Min(subEnd, data.Length))
                {
                    var subTag = Encoding.ASCII.GetString(data, subPos, 4);
                    var subSize = BitConverter.ToUInt32(data, subPos + 4);
                    var subNote = "";
                    if (subPos + 8 + subSize > data.Length) subNote = "  *** SIZE EXCEEDS FILE ***";
                    if (subPos + 8 + subSize > subEnd) subNote += "  *** SIZE EXCEEDS LIST ***";
                    Console.WriteLine($"        @{subPos,10:N0}  sub='{subTag}'  size={subSize,12:N0}{subNote}");
                    subPos += 8 + (int)subSize;
                    if ((subSize & 1) != 0) subPos++;
                    if (subTag.Length != 4 || !subTag.All(ch => ch is >= ' ' and < (char)127))
                    {
                        Console.WriteLine($"        *** non-ascii tag, stopping inner walk ***");
                        break;
                    }
                }
            }

            pos += 8 + (int)size;
            if ((size & 1) != 0) pos++;
            if (!tag.All(ch => ch is >= ' ' and < (char)127)) { Console.WriteLine("  *** non-ascii tag, stopping ***"); break; }
        }
    }
}
