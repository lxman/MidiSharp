using System;
using System.Collections.Generic;
using MidiSharp.Model;
using MidiSharp.Model.Events;
using MidiSharp.Sequencing;
using MidiSharp.SoundBank;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap;

/// <summary>
/// Determines which patches a MIDI file actually uses — "what the song would normally play."
/// Walks the sequenced event stream maintaining per-channel Bank Select / Program state and
/// records the resolved (bank, program) active at each NoteOn, so the result matches the
/// synth's playback resolution (via <see cref="BankResolution"/>).
/// </summary>
/// <remarks>
/// v1 scope: tracks Program Change and Bank Select (CC 0 / CC 32), with channel 10 defaulting
/// to the drum bank. It does not interpret GS/XG "rhythm part" SysEx that relocates drums to
/// other channels (rare in practice); such a channel is analyzed as melodic.
/// </remarks>
public static class PatchUsageAnalyzer
{
    private struct ChannelBankState
    {
        public byte BankMsb;
        public byte BankLsb;
        public byte Program;
        public bool IsDrum;
        public ushort DrumBank;
    }

    /// <summary>
    /// Analyze a song. When <paramref name="baseBank"/> is supplied, each result is annotated
    /// with the instrument name that would normally sound (applying the synth's bank-0 fallback).
    /// Results are in first-use order.
    /// </summary>
    public static IReadOnlyList<UsedPatch> Analyze(MidiFile file, IRBank? baseBank = null)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));

        var state = new ChannelBankState[16];
        for (var i = 0; i < 16; i++)
        {
            state[i].DrumBank = BankResolution.GmDrumBank;
            if (i == 9) state[i].IsDrum = true;   // channel 10 = percussion (GM default)
        }

        var order = new List<(int Bank, int Program)>();
        var channelsByKey = new Dictionary<(int, int), SortedSet<int>>();

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
                    (int bank, int) key = (bank, (int)s.Program);
                    if (!channelsByKey.TryGetValue(key, out SortedSet<int>? chans))
                    {
                        chans = [];
                        channelsByKey[key] = chans;
                        order.Add(key);
                    }
                    chans.Add(on.Channel);
                    break;
                }
            }
        }

        var result = new List<UsedPatch>(order.Count);
        foreach ((int bank, int program) in order)
        {
            string? baseName = null;
            if (baseBank != null)
            {
                Patch? patch = baseBank.FindPatch(bank, program) ?? baseBank.FindPatch(0, program);
                baseName = patch?.Name;
            }

            result.Add(new UsedPatch
            {
                Bank = bank,
                Program = program,
                Channels = new List<int>(channelsByKey[(bank, program)]),
                IsDrum = bank == BankResolution.GmDrumBank,
                BaseName = baseName,
            });
        }

        return result;
    }
}
