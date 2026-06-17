using System;
using System.IO;
using System.Linq;
using Loader;
using MidiSharp.SoundBank;
using Xunit;

namespace MidiSharp.Loader.Tests;

/// <summary>
/// Validates the opt-in memory-mapped SF2 sample backing (<see cref="SoundBankLoadOptions.MemoryMapSamples"/>):
/// it must read byte-for-byte identical sample data to the managed path, and the map → read → unmap
/// lifecycle must be crash-free under repeated load/dispose. Gated on a real SF2 being available —
/// there are no bundled SF2 fixtures, so these skip cleanly on CI and run on a developer box.
/// </summary>
public class Sf2MemoryMapTests
{
    // Smallest real SF2 (≥200 KB so it has actual sample data) under the usual soundfont roots.
    private static string? FindSf2()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(home, "soundfonts", "deduped", "sf2"),
            Path.Combine(home, "soundfonts"),
        };
        return roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*.sf2", SearchOption.AllDirectories))
            .Select(f => new FileInfo(f))
            .Where(fi => fi.Length is > 200_000 and < 60_000_000)
            .OrderBy(fi => fi.Length)
            .Select(fi => fi.FullName)
            .FirstOrDefault();
    }

    [Fact]
    public void MmapAndManaged_ProduceIdenticalSampleData()
    {
        var path = FindSf2();
        if (path == null) return;   // no real SF2 available — skip

        using var managed = SoundBankLoader.Load(path, new SoundBankLoadOptions { MemoryMapSamples = false });
        using var mapped = SoundBankLoader.Load(path, new SoundBankLoadOptions { MemoryMapSamples = true });

        Assert.Equal(managed.Samples.Count, mapped.Samples.Count);

        var a = new float[4096];
        var b = new float[4096];
        for (var id = 0; id < managed.Samples.Count; id++)
        {
            var len = managed.Samples.Metadata(id).LengthFrames;
            for (long off = 0; off < len; off += a.Length)
            {
                var na = managed.Samples.ReadFrames(id, off, a);
                var nb = mapped.Samples.ReadFrames(id, off, b);
                Assert.Equal(na, nb);
                Assert.True(a.AsSpan(0, na).SequenceEqual(b.AsSpan(0, nb)),
                    $"sample {id} mismatch at frame {off}");
            }
        }
    }

    [Fact]
    public void Mmap_LoadReadDispose_StressNoCrash()
    {
        var path = FindSf2();
        if (path == null) return;   // skip

        var buf = new float[2048];
        for (var iter = 0; iter < 40; iter++)
        {
            using var bank = SoundBankLoader.Load(path, new SoundBankLoadOptions { MemoryMapSamples = true });
            // Touch several samples so pages actually fault in and the mapped view is read...
            for (var id = 0; id < Math.Min(bank.Samples.Count, 8); id++)
                bank.Samples.ReadFrames(id, 0, buf);
            // ...then `using` disposes the bank → unmaps. Next iteration re-maps from scratch.
        }
        // Reaching here without a native crash validates the map/read/unmap lifecycle.
        Assert.True(true);
    }
}
