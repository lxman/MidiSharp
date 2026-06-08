using System;
using System.Collections.Generic;
using MidiSharp.Model;
using MidiSharp.Model.Events;
using MidiSharp.Sequencing;
using MidiSharp.SoundBank;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap;

/// <summary>
/// Determines what each MIDI track sounds like — the per-track companion to
/// <see cref="PatchUsageAnalyzer"/>. Walks the sequenced event stream maintaining per-channel
/// Bank Select / Program state (state is global per channel, exactly as the synth sees it) and
/// attributes every NoteOn to the track it came from, so a track that shares a channel/program
/// with another part is still reported as its own instrument.
/// </summary>
/// <remarks>
/// Same v1 scope as <see cref="PatchUsageAnalyzer"/>: Program Change + Bank Select (CC 0 / CC 32),
/// channel 10 defaulting to the drum bank; no GS/XG rhythm-part SysEx interpretation.
/// </remarks>
public static class TrackUsageAnalyzer
{
    private struct ChannelBankState
    {
        public byte BankMsb;
        public byte BankLsb;
        public byte Program;
        public bool IsDrum;
        public ushort DrumBank;
    }

    private sealed class TrackAccumulator
    {
        public readonly SortedSet<int> Channels = new();
        public readonly SortedSet<int> Programs = new();
        public (int Bank, int Program)? FirstPatch;
    }

    /// <summary>
    /// Analyze a song's tracks. When <paramref name="baseBank"/> is supplied, each track is
    /// annotated with the instrument name that would normally sound on it (applying the synth's
    /// bank-0 fallback). Results are in track order; tracks that sound no notes are included
    /// (with empty channels/programs) so named-but-silent tracks still surface in a UI.
    /// </summary>
    public static IReadOnlyList<TrackUsage> Analyze(MidiFile file, IRBank? baseBank = null)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));

        var state = new ChannelBankState[16];
        for (var i = 0; i < 16; i++)
        {
            state[i].DrumBank = (ushort)BankResolution.GmDrumBank;
            if (i == 9) state[i].IsDrum = true;   // channel 10 = percussion (GM default)
        }

        var accumulators = new Dictionary<int, TrackAccumulator>();

        foreach (var scheduled in new MidiSequencer(file).Events)
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
                    var s = state[on.Channel];
                    var bank = BankResolution.Resolve(s.BankMsb, s.BankLsb, s.IsDrum, s.DrumBank);
                    if (!accumulators.TryGetValue(scheduled.TrackIndex, out var acc))
                    {
                        acc = new TrackAccumulator();
                        accumulators[scheduled.TrackIndex] = acc;
                    }
                    acc.Channels.Add(on.Channel);
                    acc.Programs.Add(s.Program);
                    acc.FirstPatch ??= (bank, s.Program);
                    break;
                }
            }
        }

        var result = new List<TrackUsage>(file.Tracks.Count);
        for (var t = 0; t < file.Tracks.Count; t++)
        {
            accumulators.TryGetValue(t, out var acc);

            string? baseName = null;
            if (baseBank != null && acc?.FirstPatch is { } fp)
            {
                var patch = baseBank.FindPatch(fp.Bank, fp.Program) ?? baseBank.FindPatch(0, fp.Program);
                baseName = patch?.Name;
            }

            result.Add(new TrackUsage
            {
                TrackIndex = t,
                Name = file.Tracks[t].Name,
                Channels = acc == null ? Array.Empty<int>() : new List<int>(acc.Channels),
                Programs = acc == null ? Array.Empty<int>() : new List<int>(acc.Programs),
                BaseName = baseName,
            });
        }

        return result;
    }
}
