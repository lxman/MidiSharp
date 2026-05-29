using System;
using System.Collections.Generic;
using MidiSharp.Audio;

namespace MidiSharp.SoundBank.Sfz;

/// <summary>
/// In-memory sample source for SFZ external sample files. Each unique referenced
/// file (WAV/AIFF/FLAC/Ogg — already decoded to float by <see cref="AudioCodecs"/>)
/// is held as one buffer. Stereo files are folded to mono (averaged) because the
/// synth voices are mono; true per-voice stereo would be a synth-wide change.
/// </summary>
internal sealed class SfzSampleSource : ISampleSource
{
    private readonly float[][] _samples;
    private readonly SampleMetadata[] _metadata;

    public int Count => _samples.Length;
    public SampleMetadata Metadata(int sampleId) => _metadata[sampleId];

    public SfzSampleSource(IReadOnlyList<(DecodedAudio Audio, string Name)> decoded)
    {
        int n = decoded.Count;
        _samples = new float[n][];
        _metadata = new SampleMetadata[n];

        for (int i = 0; i < n; i++)
        {
            var (audio, name) = decoded[i];
            _samples[i] = audio.Channels <= 1 ? audio.Samples : FoldToMono(audio.Samples, audio.Channels);

            long frames = audio.FrameCount;
            _metadata[i] = new SampleMetadata
            {
                Name = name,
                SampleRate = audio.SampleRate,
                Channels = 1,
                LengthFrames = frames,
                LoopStartFrames = audio.HasLoop ? audio.LoopStartFrame : 0,
                LoopEndFrames = audio.HasLoop ? audio.LoopEndFrame : frames,
                // SFZ pitch_keycenter (per-zone, via SampleRef.OverridingRootKey)
                // governs pitch; the file's own root note is a fallback only.
                RootKey = audio.RootKey >= 0 ? audio.RootKey : 60,
                PitchCorrectionCents = audio.FineTuneCents,
            };
        }
    }

    private static float[] FoldToMono(float[] interleaved, int channels)
    {
        long frames = interleaved.Length / channels;
        var mono = new float[frames];
        float inv = 1f / channels;
        for (long f = 0; f < frames; f++)
        {
            float sum = 0f;
            long b = f * channels;
            for (int c = 0; c < channels; c++) sum += interleaved[b + c];
            mono[f] = sum * inv;
        }
        return mono;
    }

    public int ReadFrames(int sampleId, long frameOffset, Span<float> dest)
    {
        var src = _samples[sampleId];
        if (frameOffset < 0 || frameOffset >= src.Length) return 0;
        int available = (int)Math.Min(dest.Length, src.Length - frameOffset);
        new ReadOnlySpan<float>(src, (int)frameOffset, available).CopyTo(dest);
        return available;
    }

    public void PrepareSample(int sampleId) { /* Already resident in RAM. */ }

    public void Dispose() { /* No unmanaged resources. */ }
}
