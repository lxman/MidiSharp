using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Loader;
using MidiSharp.SoundBank;
using Xunit;

namespace MidiSharp.Loader.Tests;

/// <summary>
/// The SF3 decoded-sample cache rents its buffers from ArrayPool and returns them on eviction.
/// This verifies that pooling doesn't corrupt data: a sample read after its buffer has been evicted
/// (returned to the pool and reused by other decodes) must re-decode to byte-identical output.
/// Gated on a real SF3 font being present (no bundled fixtures) — skips cleanly on CI.
/// </summary>
public class Sf3CacheTests
{
    private static string? FindSf3()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(home, "soundfonts", "deduped", "sf3"),
            Path.Combine(home, "soundfonts"),
        };
        return roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*.sf3", SearchOption.AllDirectories))
            .Select(f => new FileInfo(f))
            .Where(fi => fi.Length is > 20_000 and < 20_000_000)
            .OrderBy(fi => fi.Length)
            .Select(fi => fi.FullName)
            .FirstOrDefault();
    }

    private static float[] ReadAll(ISampleSource src, int id)
    {
        // SF3 samples are mono in practice, so a frame is one float.
        var outp = new List<float>();
        var buf = new float[4096];
        long off = 0;
        while (true)
        {
            var n = src.ReadFrames(id, off, buf);
            if (n <= 0) break;
            for (var i = 0; i < n; i++) outp.Add(buf[i]);
            off += n;
        }
        return outp.ToArray();
    }

    [Fact]
    public void PooledCache_SurvivesEvictionAndReDecode()
    {
        var path = FindSf3();
        if (path == null) return;   // no real SF3 available — skip

        // Tiny cache budget forces eviction churn: buffers get returned to the pool and re-rented.
        using var bank = SoundBankLoader.Load(path, new SoundBankLoadOptions { DecodedSampleCacheBytes = 64 * 1024 });
        var src = bank.Samples;
        if (src.Count < 2) return;

        var first = ReadAll(src, 0);
        Assert.True(first.AsSpan().SequenceEqual(ReadAll(src, 0).AsSpan()), "back-to-back reads differ");

        // Churn the cache by reading every other sample, evicting sample 0 (and returning its buffer).
        for (var id = 1; id < src.Count; id++) ReadAll(src, id);

        // Re-read sample 0 — re-decoded into a pool buffer that was reused in between. Must match.
        Assert.True(first.AsSpan().SequenceEqual(ReadAll(src, 0).AsSpan()),
            "sample differs after pool eviction/reuse — pooling corrupted the data");
    }
}
