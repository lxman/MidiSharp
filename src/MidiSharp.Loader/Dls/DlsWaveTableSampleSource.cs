using System;
using System.Collections.Generic;
using MidiSharp.Audio;
using MidiSharp.SoundBank;

namespace MidiSharp.Loader.Dls;

/// <summary>
/// In-memory sample source for DLS embedded wave-pool data. Each wave's PCM
/// bytes are decoded once at load time into float32 and held as one array
/// per sample. The result is equivalent to SF2's
/// <c>MemoryMappedSf2SampleSource</c> — a flat indexable pool of mono/stereo
/// float frames.
/// </summary>
/// <remarks>
/// DLS PCM is uncompressed; upfront decode adds 2× the on-disk sample-data
/// size to working memory but avoids per-NoteOn decode latency. Most DLS
/// banks are small enough that this is fine.
/// </remarks>
internal sealed class DlsWaveTableSampleSource : ISampleSource
{
    private readonly float[][] _samples;
    private readonly SampleMetadata[] _metadata;

    public int Count => _samples.Length;
    public SampleMetadata Metadata(int sampleId) => _metadata[sampleId];

    public DlsWaveTableSampleSource(IReadOnlyList<DlsWave> waves)
    {
        int n = waves.Count;
        _samples = new float[n][];
        _metadata = new SampleMetadata[n];
        for (var i = 0; i < n; i++)
        {
            DlsWave? w = waves[i];
            bool isFloat = w.FormatTag == WaveFormatTag.IeeeFloat;
            (float[] decoded, long frames) = PcmDecoder.Decode(w.Data.Span, w.Channels, w.BitsPerSample, isFloat);
            _samples[i] = decoded;

            WaveSampleInfo? info = w.SampleInfo;
            long loopStart = 0, loopEnd = frames;
            if (info != null && info.Loops.Count > 0)
            {
                SampleLoop loop = info.Loops[0];
                loopStart = loop.StartFrame;
                loopEnd = (long)loop.StartFrame + loop.LengthFrames;
            }

            _metadata[i] = new SampleMetadata
            {
                Name = w.Name,
                SampleRate = (int)w.SampleRate,
                Channels = w.Channels,
                LengthFrames = frames,
                LoopStartFrames = loopStart,
                LoopEndFrames = loopEnd,
                RootKey = info?.UnityNote ?? 60,
                PitchCorrectionCents = info?.FineTuneCents ?? 0,
            };
        }
    }

    public int ReadFrames(int sampleId, long frameOffset, Span<float> dest)
    {
        float[] src = _samples[sampleId];
        int channels = _metadata[sampleId].Channels;
        long firstFloat = frameOffset * channels;
        if (firstFloat < 0 || firstFloat >= src.Length) return 0;

        var available = (int)Math.Min(dest.Length, src.Length - firstFloat);
        new ReadOnlySpan<float>(src, (int)firstFloat, available).CopyTo(dest);
        return available / channels;
    }

    public void PrepareSample(int sampleId) { /* Already resident in RAM. */ }

    public void Dispose() { /* No unmanaged resources. */ }
}
