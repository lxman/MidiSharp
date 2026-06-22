using System.Collections.Generic;
using MidiSharp.Model;
using MidiSharp.Model.Events;
using Xunit;

namespace MidiSharp.PatchMap.Tests;

public class PatchUsageAnalyzerTests
{
    // Build a single-track song. AbsoluteTicks ascend with array order so the sequencer
    // (which sorts on AbsoluteTicks) preserves the order events are written in.
    private static MidiFile Song(params MidiEvent[] events)
    {
        for (var i = 0; i < events.Length; i++) events[i].AbsoluteTicks = i * 10;
        var header = new MidiHeader(MidiFormat.SingleTrack, 1, TimeDivision.FromTicksPerQuarterNote(480));
        return new MidiFile(header, [new MidiTrack(events)]);
    }

    [Fact]
    public void NotesBeforeAnyProgramChange_UseProgram0Bank0()
    {
        IReadOnlyList<UsedPatch> used = PatchUsageAnalyzer.Analyze(Song(
            new NoteOnEvent { Channel = 0, Note = 60, Velocity = 100 }));

        UsedPatch p = Assert.Single(used);
        Assert.Equal(0, p.Bank);
        Assert.Equal(0, p.Program);
        Assert.False(p.IsDrum);
    }

    [Fact]
    public void ProgramChangeThenNote_RecordsThatProgram()
    {
        IReadOnlyList<UsedPatch> used = PatchUsageAnalyzer.Analyze(Song(
            new ProgramChangeEvent { Channel = 0, Program = 30 },
            new NoteOnEvent { Channel = 0, Note = 60, Velocity = 100 }));

        UsedPatch p = Assert.Single(used);
        Assert.Equal(0, p.Bank);
        Assert.Equal(30, p.Program);
    }

    [Fact]
    public void DrumChannel_ResolvesToBank128_AndIsDrum()
    {
        IReadOnlyList<UsedPatch> used = PatchUsageAnalyzer.Analyze(Song(
            new NoteOnEvent { Channel = 9, Note = 36, Velocity = 100 }));

        UsedPatch p = Assert.Single(used);
        Assert.Equal(128, p.Bank);
        Assert.True(p.IsDrum);
    }

    [Fact]
    public void BankSelectLsb_PreferredOverMsb()
    {
        IReadOnlyList<UsedPatch> used = PatchUsageAnalyzer.Analyze(Song(
            new ControlChangeEvent { Channel = 0, Controller = 0, Value = 5 },   // MSB
            new ControlChangeEvent { Channel = 0, Controller = 32, Value = 2 },  // LSB (wins)
            new ProgramChangeEvent { Channel = 0, Program = 5 },
            new NoteOnEvent { Channel = 0, Note = 60, Velocity = 100 }));

        UsedPatch p = Assert.Single(used);
        Assert.Equal(2, p.Bank);
        Assert.Equal(5, p.Program);
    }

    [Fact]
    public void SamePatchOnTwoChannels_ListsBothChannels()
    {
        IReadOnlyList<UsedPatch> used = PatchUsageAnalyzer.Analyze(Song(
            new NoteOnEvent { Channel = 0, Note = 60, Velocity = 100 },
            new NoteOnEvent { Channel = 1, Note = 64, Velocity = 100 }));   // both default to (0,0)

        UsedPatch p = Assert.Single(used);
        Assert.Equal([0, 1], p.Channels);
    }

    [Fact]
    public void VelocityZeroNoteOn_IsIgnored()
    {
        IReadOnlyList<UsedPatch> used = PatchUsageAnalyzer.Analyze(Song(
            new NoteOnEvent { Channel = 0, Note = 60, Velocity = 0 }));   // running-status note-off

        Assert.Empty(used);
    }

    [Fact]
    public void BaseName_AppliesBankZeroFallback()
    {
        using SoundBank.SoundBank baseBank = TestBanks.OneSamplePatch("base", 0.1f, bank: 0, program: 30, patchName: "Distortion Guitar");

        // The song selects bank 5 (MSB) / program 30, which the base font only has at bank 0,
        // so the name must come via the same bank-0 fallback the synth applies at NoteOn.
        IReadOnlyList<UsedPatch> used = PatchUsageAnalyzer.Analyze(Song(
            new ControlChangeEvent { Channel = 0, Controller = 0, Value = 5 },
            new ProgramChangeEvent { Channel = 0, Program = 30 },
            new NoteOnEvent { Channel = 0, Note = 60, Velocity = 100 }), baseBank);

        UsedPatch p = Assert.Single(used);
        Assert.Equal(5, p.Bank);
        Assert.Equal(30, p.Program);
        Assert.Equal("Distortion Guitar", p.BaseName);
    }
}
