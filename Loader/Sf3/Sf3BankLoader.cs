using System;
using System.Collections.Generic;
using Loader.Sf2;
using Loader.Sf2.Enums;
using Loader.Sf2.Model;
using MidiSharp.Audio;
using MidiSharp.SoundBank;

namespace Loader.Sf3;

/// <summary>
/// SF3 → IR translator. SF3 is structurally identical to SF2 — same RIFF
/// layout, same PHDR/PBAG/INST/IBAG/IMOD/IGEN/SHDR tables — so the zone and
/// hierarchy walk is delegated to <see cref="Sf2BankLoaderShared"/>. The only
/// SF3-specific work happens here: building a
/// <see cref="LazyVorbisSf3SampleSource"/> instead of a PCM one, and reading
/// each sample's Vorbis header to learn the decoded frame count without
/// decoding the audio body.
/// </summary>
internal static class Sf3BankLoader
{
    public static SoundBank Load(SoundFont sf, SoundBankLoadOptions options)
    {
        var samples = BuildSampleSource(sf, options);
        var patches = Sf2BankLoader.BuildPatches(sf);

        return new SoundBank
        {
            Name = sf.Info.BankName,
            Author = sf.Info.Engineer,
            Copyright = sf.Info.Copyright,
            Comment = sf.Info.Comments,
            SourceFormat = SoundBankFormat.Sf3,
            Patches = patches,
            Samples = samples,
        };
    }

    private static LazyVorbisSf3SampleSource BuildSampleSource(SoundFont sf, SoundBankLoadOptions options)
    {
        var smpl = sf.RawSampleBytes;
        var metadata = new SampleMetadata[sf.SampleHeaders.Count];
        var entries = new (int ByteStart, int ByteLength, int Channels)[sf.SampleHeaders.Count];

        for (int i = 0; i < sf.SampleHeaders.Count; i++)
        {
            var hdr = sf.SampleHeaders[i];

            // SF3 convention: SHDR.Start and SHDR.End are *byte* offsets into the
            // smpl chunk (not int16 frame indices as in SF2). Each [Start, End)
            // window contains one complete Ogg Vorbis bitstream.
            int byteStart = (int)hdr.Start;
            int byteEnd = (int)hdr.End;
            int byteLen = Math.Max(0, byteEnd - byteStart);

            int channels = 1;
            long decodedFrames = 0;
            if (byteLen > 0 && byteStart + byteLen <= smpl.Length)
            {
                (channels, decodedFrames) = PeekVorbisLength(smpl.Slice(byteStart, byteLen));
            }

            entries[i] = (byteStart, byteLen, channels);

            metadata[i] = new SampleMetadata
            {
                Name = hdr.Name,
                SampleRate = (int)hdr.SampleRate,
                Channels = channels,
                LengthFrames = decodedFrames,
                // Loop points in SF3 are already decoded-frame relative.
                LoopStartFrames = Math.Max(0, (long)hdr.StartLoop),
                LoopEndFrames = Math.Max(0, (long)hdr.EndLoop),
                RootKey = hdr.OriginalPitch,
                PitchCorrectionCents = hdr.PitchCorrection,
                StereoLinkSampleId = ResolveStereoLink(hdr, sf.SampleHeaders),
            };
        }

        return new LazyVorbisSf3SampleSource(smpl, metadata, entries, options.DecodedSampleCacheBytes);
    }

    /// <summary>
    /// Opens the Vorbis bitstream just enough to read its header — channels and
    /// total decoded sample count. Doesn't decode the audio body. Cost is a few
    /// μs per sample; OK to run for every SHDR at load time.
    /// </summary>
    private static (int channels, long frames) PeekVorbisLength(ReadOnlyMemory<byte> blob)
    {
        try
        {
            VorbisDecoder.Peek(blob, out int channels, out long frames);
            return (channels, frames);
        }
        catch
        {
            // Malformed or truncated Vorbis blob — return zero-length placeholder.
            return (1, 0);
        }
    }

    private static int? ResolveStereoLink(SampleHeader hdr, IReadOnlyList<SampleHeader> all)
    {
        if (hdr.SampleType is SFSampleLink.MonoSample or SFSampleLink.RomMonoSample) return null;
        if (hdr.SampleLink == 0) return null;
        int link = hdr.SampleLink;
        if (link < 0 || link >= all.Count) return null;
        return link;
    }
}
