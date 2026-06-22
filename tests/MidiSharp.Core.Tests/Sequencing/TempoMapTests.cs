using System;
using MidiSharp.IO;
using MidiSharp.Model;
using MidiSharp.Sequencing;
using Xunit;

namespace MidiSharp.Core.Tests.Sequencing;

public class TempoMapTests
{
    // 120 BPM = 500,000 microseconds per beat
    // At 96 ppqn, each tick = 500000/96 = 5208.33 microseconds
    // 96 ticks = 500,000 microseconds = 0.5 seconds = 1 quarter note

    private static readonly byte[] SimpleMidiFile =
    [
        // MThd chunk
        0x4D, 0x54, 0x68, 0x64,  // "MThd"
        0x00, 0x00, 0x00, 0x06,  // length = 6
        0x00, 0x00,              // format = 0
        0x00, 0x01,              // tracks = 1
        0x00, 0x60,              // division = 96 ppqn

        // MTrk chunk
        0x4D, 0x54, 0x72, 0x6B,  // "MTrk"
        0x00, 0x00, 0x00, 0x0B,  // length = 11 bytes

        // Events:
        0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20,  // delta=0, tempo 500000 (120 BPM)
        0x00, 0xFF, 0x2F, 0x00                     // delta=0, end of track
    ];

    [Fact]
    public void BuildFrom_SingleTempo_ReturnsCorrectBpm()
    {
        MidiFile file = MidiFileReader.Read(SimpleMidiFile);
        var tempoMap = TempoMap.BuildFrom(file);

        Assert.Equal(120.0, tempoMap.GetBpmAtTick(0), 1);
        Assert.Equal(120.0, tempoMap.GetBpmAtTick(1000), 1);
    }

    [Fact]
    public void TickToTime_AtZero_ReturnsZero()
    {
        MidiFile file = MidiFileReader.Read(SimpleMidiFile);
        var tempoMap = TempoMap.BuildFrom(file);

        TimeSpan time = tempoMap.TickToTime(0);

        Assert.Equal(TimeSpan.Zero, time);
    }

    [Fact]
    public void TickToTime_At96Ticks_ReturnsHalfSecond()
    {
        MidiFile file = MidiFileReader.Read(SimpleMidiFile);
        var tempoMap = TempoMap.BuildFrom(file);

        // 96 ticks at 96 ppqn = 1 quarter note
        // At 120 BPM, 1 quarter note = 0.5 seconds
        TimeSpan time = tempoMap.TickToTime(96);

        Assert.Equal(500, time.TotalMilliseconds, 1);
    }

    [Fact]
    public void TimeToTick_RoundTrip()
    {
        MidiFile file = MidiFileReader.Read(SimpleMidiFile);
        var tempoMap = TempoMap.BuildFrom(file);

        var originalTick = 192L;  // 2 quarter notes = 1 second at 120 BPM
        TimeSpan time = tempoMap.TickToTime(originalTick);
        long recoveredTick = tempoMap.TimeToTick(time);

        Assert.Equal(originalTick, recoveredTick);
    }

    [Fact]
    public void DefaultTempo_WhenNoTempoEvent_Is120Bpm()
    {
        // MIDI file without explicit tempo
        var noTempoFile = new byte[]
        {
            // MThd chunk
            0x4D, 0x54, 0x68, 0x64,
            0x00, 0x00, 0x00, 0x06,
            0x00, 0x00,
            0x00, 0x01,
            0x00, 0x60,  // 96 ppqn

            // MTrk chunk with just end of track
            0x4D, 0x54, 0x72, 0x6B,
            0x00, 0x00, 0x00, 0x04,
            0x00, 0xFF, 0x2F, 0x00
        };

        MidiFile file = MidiFileReader.Read(noTempoFile);
        var tempoMap = TempoMap.BuildFrom(file);

        Assert.Equal(120.0, tempoMap.GetBpmAtTick(0), 1);
    }
}
