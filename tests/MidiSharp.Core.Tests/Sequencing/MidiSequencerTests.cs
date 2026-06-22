using System;
using System.Collections.Generic;
using System.Linq;
using MidiSharp.IO;
using MidiSharp.Model;
using MidiSharp.Model.Events;
using MidiSharp.Sequencing;
using Xunit;

namespace MidiSharp.Core.Tests.Sequencing;

public class MidiSequencerTests
{
    private static readonly byte[] TestMidiFile =
    [
        // MThd chunk - Format 0, 1 track, 96 ppqn
        0x4D, 0x54, 0x68, 0x64,
        0x00, 0x00, 0x00, 0x06,
        0x00, 0x00,
        0x00, 0x01,
        0x00, 0x60,

        // MTrk chunk
        0x4D, 0x54, 0x72, 0x6B,
        0x00, 0x00, 0x00, 0x16,  // 22 bytes

        // Events:
        0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20,  // tempo 500000 (120 BPM)
        0x00, 0xC0, 0x05,                          // program change
        0x00, 0x90, 0x3C, 0x60,                    // note on
        0x60, 0x80, 0x3C, 0x40,                    // note off at tick 96
        0x00, 0xFF, 0x2F, 0x00                     // end of track
    ];

    [Fact]
    public void Constructor_ValidFile_BuildsTimeline()
    {
        MidiFile file = MidiFileReader.Read(TestMidiFile);
        var sequencer = new MidiSequencer(file);

        Assert.Equal(5, sequencer.Events.Count);
    }

    [Fact]
    public void Duration_ReturnsCorrectValue()
    {
        MidiFile file = MidiFileReader.Read(TestMidiFile);
        var sequencer = new MidiSequencer(file);

        // Last event at tick 96, at 120 BPM = 0.5 seconds
        Assert.Equal(500, sequencer.Duration.TotalMilliseconds, 10);
    }

    [Fact]
    public void Events_AreSortedByTime()
    {
        MidiFile file = MidiFileReader.Read(TestMidiFile);
        var sequencer = new MidiSequencer(file);

        long previousTick = -1;
        foreach (ScheduledEvent evt in sequencer.Events)
        {
            Assert.True(evt.AbsoluteTicks >= previousTick);
            previousTick = evt.AbsoluteTicks;
        }
    }

    [Fact]
    public void GetPlayableEvents_ExcludesMetaEvents()
    {
        MidiFile file = MidiFileReader.Read(TestMidiFile);
        var sequencer = new MidiSequencer(file);

        List<ScheduledEvent> playable = sequencer.GetPlayableEvents().ToList();

        // Should have program change, note on, note off (3 events)
        // Excluding tempo and end of track meta events
        Assert.Equal(3, playable.Count);
        Assert.All(playable, e => Assert.IsNotType<MetaEvent>(e.Event));
    }

    [Fact]
    public void GetEventsFrom_ReturnsEventsAfterTime()
    {
        MidiFile file = MidiFileReader.Read(TestMidiFile);
        var sequencer = new MidiSequencer(file);

        // Get events from 250ms onwards (half a quarter note at 120 BPM)
        List<ScheduledEvent> fromMiddle = sequencer.GetEventsFrom(TimeSpan.FromMilliseconds(250)).ToList();

        // Should get note off and end of track
        Assert.Equal(2, fromMiddle.Count);
        Assert.Equal(96, fromMiddle[0].AbsoluteTicks);
    }

    [Fact]
    public void TickToTime_DelegatesToTempoMap()
    {
        MidiFile file = MidiFileReader.Read(TestMidiFile);
        var sequencer = new MidiSequencer(file);

        TimeSpan time = sequencer.TickToTime(96);

        Assert.Equal(500, time.TotalMilliseconds, 1);
    }

    [Fact]
    public void GetEventIndexAtTime_ReturnsCorrectIndex()
    {
        MidiFile file = MidiFileReader.Read(TestMidiFile);
        var sequencer = new MidiSequencer(file);

        // Events at tick 0: tempo, program change, note on (indices 0, 1, 2)
        // Events at tick 96: note off, end of track (indices 3, 4)

        int index = sequencer.GetEventIndexAtTime(TimeSpan.FromMilliseconds(250));

        // Should return index of first event at or after tick 48 (250ms)
        // That would be index 3 (note off at tick 96)
        Assert.Equal(3, index);
    }
}
