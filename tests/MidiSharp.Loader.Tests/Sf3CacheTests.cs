using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Loader;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

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
    public void Stereo_sample_reads_full_frames_without_overflow()
    {
        // Scan available SF3 fonts for one that actually carries a stereo sample (FluidR3Mono and many
        // GM banks are all-mono); only that exercises the channel-aware copy. Skips cleanly if none.
        var (bank, src, stereoId) = FindStereoSf3();
        using (bank)
        {
            if (bank == null) return;   // no SF3 with a stereo sample available — skip

            long expected = src!.Metadata(stereoId).LengthFrames;
            // Interleaved buffer sized to a quarter of the sample so several reads hit the cap. Pre-fix,
            // framesAvailable came from dest.Length (floats) instead of dest.Length/channels (frames), so
            // a capped read copied 2× the buffer and overflowed it (crash) / miscounted frames.
            int bufFrames = Math.Max(2, (int)(expected / 4));
            var buf = new float[bufFrames * 2];
            long total = 0;
            long off = 0;
            while (true)
            {
                int n = src.ReadFrames(stereoId, off, buf);   // must never write past buf
                if (n <= 0) break;
                Assert.True(n <= buf.Length / 2, $"returned {n} frames into a {buf.Length / 2}-frame buffer");
                total += n;
                off += n;
            }
            Assert.Equal(expected, total);   // every frame readable, exactly once
        }
    }

    /// <summary>Loads SF3 fonts until one with a stereo sample is found; returns (null, ...) if none.</summary>
    private static (IRBank? bank, ISampleSource? src, int stereoId) FindStereoSf3()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { Path.Combine(home, "soundfonts", "deduped", "sf3"), Path.Combine(home, "soundfonts") };
        var fonts = roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*.sf3", SearchOption.AllDirectories))
            .Select(f => new FileInfo(f))
            .Where(fi => fi.Length is > 20_000 and < 30_000_000)
            .OrderBy(fi => fi.Length)
            .Select(fi => fi.FullName);

        foreach (var path in fonts)
        {
            IRBank bank;
            try { bank = SoundBankLoader.Load(path); }
            catch { continue; }
            for (int id = 0; id < bank.Samples.Count; id++)
            {
                var m = bank.Samples.Metadata(id);
                if (m.Channels == 2 && m.LengthFrames > 8)   // a 2-channel sample big enough to span >1 read
                    return (bank, bank.Samples, id);
            }
            bank.Dispose();
        }
        return (null, null, -1);
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
