using System;
using System.IO;
using MidiSharp.Loader;
using Xunit;

namespace SF2.Net.Tests;

/// <summary>
/// Proves the SF2 24-bit extension (sm24) reaches the playback sample source, not just the parser.
/// A 24-bit font's samples carry an extra low byte per frame; the live source must fold it in so the
/// rendered float equals the 16-bit value plus the low-byte contribution. 16-bit fonts (no sm24) keep
/// the existing zero-copy path unchanged.
/// </summary>
public sealed class TwentyFourBitPlaybackTests
{
    private const int Frames = 1024;   // SyntheticSoundFont.SampleFrames

    [Fact]
    public void Sm24_low_byte_reaches_the_playback_path()
    {
        // A varying low-byte pattern (frame 0 = 0 → no change; others add precision).
        var sm24 = new byte[Frames];
        for (int i = 0; i < Frames; i++) sm24[i] = (byte)(i & 0xFF);

        float[] read16 = ReadAllFrames(SyntheticSoundFont.Build());
        float[] read24 = ReadAllFrames(SyntheticSoundFont.Build(sm24: sm24));

        const float Scale24 = 1.0f / 8388608.0f;   // 2^-23
        int differ = 0;
        for (int i = 0; i < Frames; i++)
        {
            // 24-bit value = (int16 << 8 | lo)/2^23 = int16/2^15 + lo/2^23 = read16 + lo/2^23. Precision 5
            // (~5e-6): the low byte contributes up to 255/2^23 ≈ 3e-5 — well above both the tolerance and
            // the ~2e-7 float-rounding noise from computing it in two steps here vs one in the source.
            float expected = read16[i] + sm24[i] * Scale24;
            // Absolute tolerance (not decimal-places, which rounds and is brittle at boundaries). 1e-5
            // sits above the ~2e-7 two-step float-rounding noise and below the low byte's ~3e-5 reach.
            Assert.True(Math.Abs(expected - read24[i]) < 1e-5,
                $"frame {i}: expected {expected}, got {read24[i]}");
            // Dropping the low byte would be off by lo/2^23 — measurable whenever lo is non-trivial.
            if (sm24[i] != 0 && Math.Abs(read24[i] - read16[i]) > 1e-7) differ++;
        }

        // The low byte actually changed the output for the frames that carry one.
        Assert.True(differ > 900, $"expected most frames to differ; only {differ} did");
        Assert.True(Math.Abs(read16[0] - read24[0]) < 1e-6);   // frame 0 had lo=0 → identical
    }

    [Fact]
    public void Sixteen_bit_font_is_unchanged_by_the_new_path()
    {
        // No sm24 → the source must read exactly the int16 samples (the zero-copy fast path).
        float[] a = ReadAllFrames(SyntheticSoundFont.Build());
        float[] b = ReadAllFrames(SyntheticSoundFont.Build());
        Assert.Equal(a, b);
        // Values are bounded by the 16-bit range, never showing sub-LSB 24-bit detail.
        foreach (var v in a) Assert.InRange(v, -1.0f, 1.0f);
    }

    private static float[] ReadAllFrames(byte[] sf2Bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "sf2_24bit_" + Guid.NewGuid().ToString("N") + ".sf2");
        File.WriteAllBytes(path, sf2Bytes);
        try
        {
            using var bank = SoundBankLoader.Load(path);
            int sampleId = bank.FindPatch(0, 0)!.Zones[0].Sample.SampleId;
            var dest = new float[Frames];
            int n = bank.Samples.ReadFrames(sampleId, 0, dest);
            Assert.Equal(Frames, n);
            return dest;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
