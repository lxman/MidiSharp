using MidiSharp.IO;
using MidiSharp.Model;
using MidiSharp.Model.Events;
using Xunit;

namespace MidiSharp.Core.Tests.IO;

public class MidiFileReaderTests
{
    // Minimal Format 0 MIDI file from the SMF specification example
    // MThd (format 0, 1 track, 96 ppqn)
    // MTrk with time signature, tempo, program changes, notes, end of track
    private static readonly byte[] MinimalMidiFile =
    [
        // MThd chunk
        0x4D, 0x54, 0x68, 0x64,  // "MThd"
        0x00, 0x00, 0x00, 0x06,  // length = 6
        0x00, 0x00,              // format = 0
        0x00, 0x01,              // tracks = 1
        0x00, 0x60,              // division = 96 ppqn

        // MTrk chunk
        0x4D, 0x54, 0x72, 0x6B,  // "MTrk"
        0x00, 0x00, 0x00, 0x16,  // length = 22 bytes

        // Events:
        0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20,  // delta=0, tempo 500000 (120 BPM)
        0x00, 0xC0, 0x05,                          // delta=0, program change ch1 = 5
        0x00, 0x90, 0x3C, 0x60,                    // delta=0, note on ch1 C4 vel=96
        0x60, 0x80, 0x3C, 0x40,                    // delta=96, note off ch1 C4 vel=64
        0x00, 0xFF, 0x2F, 0x00                     // delta=0, end of track
    ];

    [Fact]
    public void Read_ValidMidiFile_ReturnsCorrectHeader()
    {
        MidiFile file = MidiFileReader.Read(MinimalMidiFile);

        Assert.Equal(MidiFormat.SingleTrack, file.Header.Format);
        Assert.Equal(1, file.Header.TrackCount);
        Assert.Equal(96, file.Header.Division.TicksPerQuarterNote);
        Assert.False(file.Header.Division.IsSmpte);
    }

    [Fact]
    public void Read_ValidMidiFile_ReturnsCorrectTrackCount()
    {
        MidiFile file = MidiFileReader.Read(MinimalMidiFile);

        Assert.Single(file.Tracks);
    }

    [Fact]
    public void Read_ValidMidiFile_ParsesTempoEvent()
    {
        MidiFile file = MidiFileReader.Read(MinimalMidiFile);
        MidiTrack track = file.Tracks[0];

        var tempoEvent = Assert.IsType<MetaEvent>(track.Events[0]);
        Assert.Equal(MetaEventType.SetTempo, tempoEvent.Type);
        Assert.Equal(500000, tempoEvent.Tempo);
        Assert.Equal(120.0, tempoEvent.Bpm);
    }

    [Fact]
    public void Read_ValidMidiFile_ParsesProgramChange()
    {
        MidiFile file = MidiFileReader.Read(MinimalMidiFile);
        MidiTrack track = file.Tracks[0];

        var programChange = Assert.IsType<ProgramChangeEvent>(track.Events[1]);
        Assert.Equal(0, programChange.Channel);
        Assert.Equal(5, programChange.Program);
    }

    [Fact]
    public void Read_ValidMidiFile_ParsesNoteOnOff()
    {
        MidiFile file = MidiFileReader.Read(MinimalMidiFile);
        MidiTrack track = file.Tracks[0];

        var noteOn = Assert.IsType<NoteOnEvent>(track.Events[2]);
        Assert.Equal(0, noteOn.Channel);
        Assert.Equal(0x3C, noteOn.Note);  // C4 (middle C)
        Assert.Equal(0x60, noteOn.Velocity);
        Assert.Equal(0, noteOn.DeltaTicks);

        var noteOff = Assert.IsType<NoteOffEvent>(track.Events[3]);
        Assert.Equal(0, noteOff.Channel);
        Assert.Equal(0x3C, noteOff.Note);
        Assert.Equal(0x40, noteOff.Velocity);
        Assert.Equal(96, noteOff.DeltaTicks);
    }

    [Fact]
    public void Read_ValidMidiFile_ParsesEndOfTrack()
    {
        MidiFile file = MidiFileReader.Read(MinimalMidiFile);
        MidiTrack track = file.Tracks[0];

        var endOfTrack = Assert.IsType<MetaEvent>(track.Events[^1]);
        Assert.Equal(MetaEventType.EndOfTrack, endOfTrack.Type);
    }

    [Fact]
    public void Read_ValidMidiFile_CalculatesAbsoluteTicks()
    {
        MidiFile file = MidiFileReader.Read(MinimalMidiFile);
        MidiTrack track = file.Tracks[0];

        Assert.Equal(0, track.Events[0].AbsoluteTicks);   // tempo
        Assert.Equal(0, track.Events[1].AbsoluteTicks);   // program change
        Assert.Equal(0, track.Events[2].AbsoluteTicks);   // note on
        Assert.Equal(96, track.Events[3].AbsoluteTicks);  // note off (after 96 ticks)
        Assert.Equal(96, track.Events[4].AbsoluteTicks);  // end of track
    }
}
