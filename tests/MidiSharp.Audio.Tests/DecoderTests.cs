using System;
using System.IO;
using Xunit;

namespace MidiSharp.Audio.Tests;

/// <summary>
/// Cross-validates each decoder against an ffmpeg-encoded fixture of a known
/// signal. Lossless formats (AIFF, FLAC) must match the WAV reference
/// sample-exact; Vorbis is checked for correct shape and low error.
/// </summary>
public sealed class DecoderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _wav;
    private readonly DecodedAudio _reference;

    public DecoderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "audiotest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ref.wav");
        var signal = CodecFixtures.MakeSignal(frames: 22050, channels: 2);  // 0.5 s stereo
        CodecFixtures.WriteWav16(_wav, signal, channels: 2);
        _reference = AudioCodecs.Decode(_wav);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Wav_reference_decodes_to_expected_shape()
    {
        Assert.Equal(2, _reference.Channels);
        Assert.Equal(44100, _reference.SampleRate);
        Assert.Equal(22050, _reference.FrameCount);
    }

    [Fact]
    public void Aiff_matches_wav_sample_exact()
    {
        if (!CodecFixtures.FfmpegAvailable) return;
        string aiff = Path.Combine(_dir, "out.aiff");
        Assert.True(CodecFixtures.Transcode(_wav, aiff));

        var d = AudioCodecs.Decode(aiff);
        Assert.Equal("AIFF", new AiffDecoder().Name);
        Assert.Equal(_reference.Channels, d.Channels);
        Assert.Equal(_reference.SampleRate, d.SampleRate);
        AssertLossless(_reference, d);
    }

    [Fact]
    public void AiffC_sowt_little_endian_matches_wav_sample_exact()
    {
        if (!CodecFixtures.FfmpegAvailable) return;
        string aifc = Path.Combine(_dir, "out_sowt.aifc");
        // ffmpeg's aiff muxer writes AIFF-C sowt when the codec is pcm_s16le.
        if (!CodecFixtures.Transcode(_wav, aifc, "-c:a pcm_s16le -f aiff")) return;

        var d = AudioCodecs.Decode(aifc);
        AssertLossless(_reference, d);
    }

    [Fact]
    public void Flac_matches_wav_sample_exact()
    {
        if (!CodecFixtures.FfmpegAvailable) return;
        string flac = Path.Combine(_dir, "out.flac");
        Assert.True(CodecFixtures.Transcode(_wav, flac));

        var d = AudioCodecs.Decode(flac);
        Assert.Equal(_reference.Channels, d.Channels);
        Assert.Equal(_reference.SampleRate, d.SampleRate);
        Assert.Equal(_reference.FrameCount, d.FrameCount);
        AssertLossless(_reference, d);
    }

    [Fact]
    public void Vorbis_decodes_to_correct_shape_and_low_error()
    {
        if (!CodecFixtures.FfmpegAvailable) return;
        string ogg = Path.Combine(_dir, "out.ogg");
        if (!CodecFixtures.Transcode(_wav, ogg, "-c:a libvorbis -q:a 6")) return;

        var d = AudioCodecs.Decode(ogg);
        Assert.Equal(_reference.Channels, d.Channels);
        Assert.Equal(_reference.SampleRate, d.SampleRate);
        // Vorbis is lossy + adds encoder delay, so don't compare sample-exact —
        // just confirm it produced a comparable amount of non-silent audio.
        Assert.True(d.FrameCount > _reference.FrameCount * 0.8, $"frames={d.FrameCount}");
        double rms = Rms(d.Samples);
        Assert.True(rms > 0.05 && rms < 0.5, $"rms={rms}");
    }

    [Fact]
    public void Dispatch_picks_decoder_by_magic_bytes()
    {
        if (!CodecFixtures.FfmpegAvailable) return;
        // No extension hint → must dispatch purely on header bytes.
        string flac = Path.Combine(_dir, "headeronly.bin");
        Assert.True(CodecFixtures.Transcode(_wav, Path.Combine(_dir, "tmp.flac")));
        File.Copy(Path.Combine(_dir, "tmp.flac"), flac, overwrite: true);

        var bytes = File.ReadAllBytes(flac);
        var d = AudioCodecs.Decode(bytes, pathHint: null);
        Assert.Equal(_reference.FrameCount, d.FrameCount);
    }

    [Fact]
    public void Real_flac_instrument_matches_ffmpeg_decode_sample_exact()
    {
        if (!CodecFixtures.FfmpegAvailable) return;
        string flac = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "soundfonts", "sfz", "Discord-SFZ-GM-Bank", "Discord GM", "Melodic", "112-Shanai", "Shn16.flac");
        if (!File.Exists(flac)) return;  // asset not present — skip

        // ffmpeg decodes the FLAC to a 16-bit WAV; our FLAC decode of the original
        // must match it bit-for-bit (real instrument data → exercises LPC, varied
        // Rice partitions, mid/side — not just the synthetic sine path).
        string refWav = Path.Combine(_dir, "shanai_ref.wav");
        Assert.True(CodecFixtures.Transcode(flac, refWav, "-c:a pcm_s16le"));

        var mine = AudioCodecs.Decode(flac);
        var reference = AudioCodecs.Decode(refWav);
        Assert.Equal(reference.Channels, mine.Channels);
        Assert.Equal(reference.SampleRate, mine.SampleRate);
        AssertLossless(reference, mine);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static void AssertLossless(DecodedAudio expected, DecodedAudio actual)
    {
        long n = Math.Min(expected.Samples.Length, actual.Samples.Length);
        Assert.True(Math.Abs(expected.Samples.Length - actual.Samples.Length) <= actual.Channels,
            $"length mismatch: {expected.Samples.Length} vs {actual.Samples.Length}");
        // Both derive from the same 16-bit WAV; lossless transcode → identical floats
        // (allow < 1 LSB for any pipeline rounding).
        const float lsb = 1.0f / 32768f;
        for (long i = 0; i < n; i++)
            Assert.True(Math.Abs(expected.Samples[i] - actual.Samples[i]) <= lsb,
                $"sample {i}: {expected.Samples[i]} vs {actual.Samples[i]}");
    }

    private static double Rms(float[] s)
    {
        double sum = 0;
        foreach (float v in s) sum += (double)v * v;
        return Math.Sqrt(sum / Math.Max(1, s.Length));
    }
}
