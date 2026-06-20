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
        var aiff = Path.Combine(_dir, "out.aiff");
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
        var aifc = Path.Combine(_dir, "out_sowt.aifc");
        // ffmpeg's aiff muxer writes AIFF-C sowt when the codec is pcm_s16le.
        if (!CodecFixtures.Transcode(_wav, aifc, "-c:a pcm_s16le -f aiff")) return;

        var d = AudioCodecs.Decode(aifc);
        AssertLossless(_reference, d);
    }

    [Fact]
    public void Flac_matches_wav_sample_exact()
    {
        if (!CodecFixtures.FfmpegAvailable) return;
        var flac = Path.Combine(_dir, "out.flac");
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
        var ogg = Path.Combine(_dir, "out.ogg");
        if (!CodecFixtures.Transcode(_wav, ogg, "-c:a libvorbis -q:a 6")) return;

        var d = AudioCodecs.Decode(ogg);
        Assert.Equal(_reference.Channels, d.Channels);
        Assert.Equal(_reference.SampleRate, d.SampleRate);
        // Vorbis is lossy + adds encoder delay, so don't compare sample-exact —
        // just confirm it produced a comparable amount of non-silent audio.
        Assert.True(d.FrameCount > _reference.FrameCount * 0.8, $"frames={d.FrameCount}");
        var rms = Rms(d.Samples);
        Assert.True(rms > 0.05 && rms < 0.5, $"rms={rms}");
    }

    [Fact]
    public void VorbisDecodePcmInto_matches_DecodePcm()
    {
        if (!CodecFixtures.FfmpegAvailable) return;
        var ogg = Path.Combine(_dir, "into.ogg");
        if (!CodecFixtures.Transcode(_wav, ogg, "-c:a libvorbis -q:a 6")) return;

        var bytes = File.ReadAllBytes(ogg);
        var reference = VorbisDecoder.DecodePcm(bytes, out _, out _, out _);

        VorbisDecoder.Peek(bytes, out var ch, out var frames);
        var buf = new float[(int)(frames * ch)];
        var n = VorbisDecoder.DecodePcmInto(bytes, buf, buf.Length, out _, out _);

        Assert.Equal(reference.Length, n);
        Assert.True(buf.AsSpan(0, n).SequenceEqual(reference.AsSpan()), "DecodePcmInto differs from DecodePcm");
    }

    [Fact]
    public void Peek_matches_decode_for_all_formats()
    {
        if (!CodecFixtures.FfmpegAvailable) return;
        foreach (var (ext, args) in new[]
        {
            ("wav", "-c:a pcm_s16le"),
            ("flac", "-c:a flac"),
            ("aiff", "-c:a pcm_s16be"),
            ("ogg", "-c:a libvorbis -q:a 6"),
        })
        {
            var f = Path.Combine(_dir, "peek." + ext);
            if (!CodecFixtures.Transcode(_wav, f, args)) continue;

            var dec = AudioCodecs.Decode(f);
            var info = AudioCodecs.Peek(f);

            Assert.Equal(dec.Channels, info.Channels);
            Assert.Equal(dec.SampleRate, info.SampleRate);
            if (ext == "ogg")   // Vorbis adds encoder delay/padding; frame counts are close, not exact
                Assert.True(Math.Abs(info.FrameCount - dec.FrameCount) < dec.FrameCount * 0.2 + 2048,
                    $"ogg frames peek={info.FrameCount} dec={dec.FrameCount}");
            else
                Assert.Equal(dec.FrameCount, info.FrameCount);
        }
    }

    [Fact]
    public void Dispatch_picks_decoder_by_magic_bytes()
    {
        if (!CodecFixtures.FfmpegAvailable) return;
        // No extension hint → must dispatch purely on header bytes.
        var flac = Path.Combine(_dir, "headeronly.bin");
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
        var flac = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "soundfonts", "sfz", "Discord-SFZ-GM-Bank", "Discord GM", "Melodic", "112-Shanai", "Shn16.flac");
        if (!File.Exists(flac)) return;  // asset not present — skip

        // ffmpeg decodes the FLAC to a 16-bit WAV; our FLAC decode of the original
        // must match it bit-for-bit (real instrument data → exercises LPC, varied
        // Rice partitions, mid/side — not just the synthetic sine path).
        var refWav = Path.Combine(_dir, "shanai_ref.wav");
        Assert.True(CodecFixtures.Transcode(flac, refWav, "-c:a pcm_s16le"));

        var mine = AudioCodecs.Decode(flac);
        var reference = AudioCodecs.Decode(refWav);
        Assert.Equal(reference.Channels, mine.Channels);
        Assert.Equal(reference.SampleRate, mine.SampleRate);
        AssertLossless(reference, mine);
    }

    [Fact]
    public void Flac_reserved_channel_assignment_is_rejected()
    {
        if (!CodecFixtures.FfmpegAvailable) return;
        var flac = Path.Combine(_dir, "reserved.flac");
        if (!CodecFixtures.Transcode(_wav, flac)) return;
        var bytes = File.ReadAllBytes(flac);

        var frame = FirstFrameOffset(bytes);
        // Sanity: a FLAC frame begins with the 0xFF F8/F9 sync; bail if we mis-located it.
        Assert.True(frame > 0 && frame + 3 < bytes.Length && bytes[frame] == 0xFF && (bytes[frame + 1] & 0xFC) == 0xF8,
            "could not locate the first FLAC frame header");

        // Byte 3 high nibble = channel assignment; force a reserved value (12). CRCs aren't verified, and
        // the guard fires before subframe decoding, so the decoder must reject it rather than misread 2ch.
        bytes[frame + 3] = (byte)((bytes[frame + 3] & 0x0F) | 0xC0);
        File.WriteAllBytes(flac, bytes);

        Assert.ThrowsAny<Exception>(() => AudioCodecs.Decode(flac));
    }

    // Offset of the first audio frame: skip "fLaC" then the metadata blocks (each = 1 flag/type byte +
    // 3-byte big-endian length + body; the block with the high flag bit set is the last).
    private static int FirstFrameOffset(byte[] b)
    {
        if (b.Length < 4 || b[0] != (byte)'f' || b[1] != (byte)'L' || b[2] != (byte)'a' || b[3] != (byte)'C')
            return -1;
        var p = 4;
        while (p + 4 <= b.Length)
        {
            var last = (b[p] & 0x80) != 0;
            var len = (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
            p += 4 + len;
            if (last) break;
        }
        return p;
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
        foreach (var v in s) sum += (double)v * v;
        return Math.Sqrt(sum / Math.Max(1, s.Length));
    }
}
