using System;
using System.Collections.Generic;
using System.Linq;
using MidiSharp.SoundBank;

namespace MidiSharp.PatchMap;

/// <summary>
/// Presents several <see cref="ISampleSource"/>s as one, concatenating their sample-id spaces
/// with a fixed per-source offset. A sample's stereo-link id (in metadata) is rewritten into
/// the concatenated space. Dispatch is O(1) via precomputed maps; the hot path allocates nothing.
/// </summary>
/// <remarks>
/// This is a borrowed view: <see cref="Dispose"/> is a no-op. The underlying sources are owned
/// by the fonts they came from — and by the <see cref="PatchMapSession"/> that holds those fonts.
/// </remarks>
internal sealed class ConcatenatedSampleSource : ISampleSource
{
    private readonly ISampleSource[] _sources;
    private readonly int[] _sourceOf;            // global id -> index into _sources
    private readonly int[] _localOf;             // global id -> local id within that source
    private readonly SampleMetadata[] _metadata; // global id -> metadata (stereo links rebased)
    private readonly int _count;

    public ConcatenatedSampleSource(IReadOnlyList<ISampleSource> sources)
    {
        _sources = sources.ToArray();

        var total = 0;
        foreach (var s in _sources) total += s.Count;
        _count = total;

        _sourceOf = new int[total];
        _localOf = new int[total];
        _metadata = new SampleMetadata[total];

        var offset = 0;
        for (var si = 0; si < _sources.Length; si++)
        {
            var src = _sources[si];
            for (var li = 0; li < src.Count; li++)
            {
                var gid = offset + li;
                _sourceOf[gid] = si;
                _localOf[gid] = li;
                var md = src.Metadata(li);
                _metadata[gid] = md.StereoLinkSampleId is int link ? Rebase(md, link + offset) : md;
            }
            offset += src.Count;
        }
    }

    public int Count => _count;

    public SampleMetadata Metadata(int sampleId) => _metadata[sampleId];

    public int ReadFrames(int sampleId, long frameOffset, Span<float> dest)
        => _sources[_sourceOf[sampleId]].ReadFrames(_localOf[sampleId], frameOffset, dest);

    public void PrepareSample(int sampleId)
        => _sources[_sourceOf[sampleId]].PrepareSample(_localOf[sampleId]);

    // Borrowed view — the session owns and disposes the underlying fonts/sources.
    public void Dispose()
    {
    }

    private static SampleMetadata Rebase(SampleMetadata m, int newStereoLink) => new()
    {
        Name = m.Name,
        SampleRate = m.SampleRate,
        Channels = m.Channels,
        LengthFrames = m.LengthFrames,
        LoopStartFrames = m.LoopStartFrames,
        LoopEndFrames = m.LoopEndFrames,
        RootKey = m.RootKey,
        PitchCorrectionCents = m.PitchCorrectionCents,
        StereoLinkSampleId = newStereoLink,
    };
}
