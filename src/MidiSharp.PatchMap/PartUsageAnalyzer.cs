using System;
using System.Collections.Generic;
using System.Linq;
using MidiSharp.Model;
using MidiSharp.Model.Events;
using MidiSharp.Sequencing;
using MidiSharp.SoundBank;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap;

/// <summary>
/// One playable "part" of a song — the finest grouping the file actually separates the music into:
/// a <c>(track, channel)</c> pair. This adapts to whatever the file provides: a well-formed multi-track
/// file yields one part per track (each on its own channel), while a single-track (format 0) file yields
/// one part per channel. Either way every distinct instrument the song uses surfaces, named from the best
/// information available — the track name when it uniquely identifies the part, otherwise the program's
/// instrument name.
/// </summary>
public sealed class PartUsage
{
    public int TrackIndex { get; init; }
    public int Channel { get; init; }

    /// <summary>The source track's name (TrackName meta), or null. Only uniquely names this part when the
    /// track sounds on a single channel (<see cref="TrackChannelCount"/> == 1).</summary>
    public string? TrackName { get; init; }

    /// <summary>How many channels the source track sounds on. 1 ⇒ the track name labels this part; &gt;1 ⇒
    /// the track is a merged/format-0 lump and each channel-part is named by its program instead.</summary>
    public int TrackChannelCount { get; init; }

    /// <summary>Distinct resolved programs this part plays (ascending).</summary>
    public IReadOnlyList<int> Programs { get; init; } = [];

    /// <summary>True when this part is percussion (channel 10 / drum bank).</summary>
    public bool IsDrum { get; init; }

    /// <summary>The base font's name for what this part plays first (after bank-0 fallback), or null.</summary>
    public string? BaseName { get; init; }
}

/// <summary>
/// Derives a song's <see cref="PartUsage"/> list — the per-<c>(track, channel)</c> companion to the
/// per-track <see cref="TrackUsageAnalyzer"/> and per-patch <see cref="PatchUsageAnalyzer"/>. Same v1
/// scope: Program Change + Bank Select (CC 0/32), channel 10 defaulting to the drum bank.
/// </summary>
public static class PartUsageAnalyzer
{
    private struct ChannelBankState
    {
        public byte BankMsb;
        public byte BankLsb;
        public byte Program;
        public bool IsDrum;
        public ushort DrumBank;
    }

    private sealed class PartAccumulator
    {
        public readonly SortedSet<int> Programs = [];
        public (int Bank, int Program)? FirstPatch;
        public bool IsDrum;
    }

    /// <summary>
    /// Analyze a song into parts, in (track, channel) order. When <paramref name="baseBank"/> is
    /// supplied, each part carries the instrument name that would normally sound on it.
    /// </summary>
    public static IReadOnlyList<PartUsage> Analyze(MidiFile file, IRBank? baseBank = null)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));

        var state = new ChannelBankState[16];
        for (var i = 0; i < 16; i++)
        {
            state[i].DrumBank = BankResolution.GmDrumBank;
            if (i == 9) state[i].IsDrum = true;   // channel 10 = percussion (GM default)
        }

        var parts = new Dictionary<(int Track, int Channel), PartAccumulator>();
        var trackChannels = new Dictionary<int, HashSet<int>>();

        foreach (ScheduledEvent scheduled in new MidiSequencer(file).Events)
        {
            switch (scheduled.Event)
            {
                case ProgramChangeEvent pc:
                    state[pc.Channel].Program = pc.Program;
                    break;
                case ControlChangeEvent { Controller: 0 } cc:
                    state[cc.Channel].BankMsb = cc.Value;
                    break;
                case ControlChangeEvent { Controller: 32 } cc:
                    state[cc.Channel].BankLsb = cc.Value;
                    break;
                case NoteOnEvent { Velocity: > 0 } on:
                {
                    ChannelBankState s = state[on.Channel];
                    int bank = BankResolution.Resolve(s.BankMsb, s.BankLsb, s.IsDrum, s.DrumBank);
                    (int TrackIndex, byte Channel) key = (scheduled.TrackIndex, on.Channel);
                    if (!parts.TryGetValue(key, out PartAccumulator? acc))
                    {
                        acc = new PartAccumulator();
                        parts[key] = acc;
                    }
                    acc.Programs.Add(s.Program);
                    acc.FirstPatch ??= (bank, s.Program);
                    acc.IsDrum = s.IsDrum;
                    if (!trackChannels.TryGetValue(scheduled.TrackIndex, out HashSet<int>? chans))
                        trackChannels[scheduled.TrackIndex] = chans = [];
                    chans.Add(on.Channel);
                    break;
                }
            }
        }

        var result = new List<PartUsage>(parts.Count);
        foreach (((int Track, int Channel) key, PartAccumulator? acc) in parts.OrderBy(p => p.Key.Track).ThenBy(p => p.Key.Channel))
        {
            string? baseName = null;
            if (baseBank != null && acc.FirstPatch is { } fp)
            {
                Patch? patch = baseBank.FindPatch(fp.Bank, fp.Program) ?? baseBank.FindPatch(0, fp.Program);
                baseName = patch?.Name;
            }

            result.Add(new PartUsage
            {
                TrackIndex = key.Track,
                Channel = key.Channel,
                TrackName = key.Track < file.Tracks.Count ? file.Tracks[key.Track].Name : null,
                TrackChannelCount = trackChannels[key.Track].Count,
                Programs = new List<int>(acc.Programs),
                IsDrum = acc.IsDrum,
                BaseName = baseName,
            });
        }
        return result;
    }
}
