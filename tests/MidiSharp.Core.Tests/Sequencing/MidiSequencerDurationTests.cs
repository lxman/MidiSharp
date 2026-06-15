using MidiSharp.Model;
using MidiSharp.Model.Events;
using MidiSharp.Sequencing;
using Xunit;

namespace MidiSharp.Core.Tests.Sequencing;

/// <summary>
/// Duration is anchored on the last NOTE event, not the last event of any track, so a conductor
/// track whose EndOfTrack is padded far past the music doesn't report minutes of trailing silence.
/// These cover the fix plus the legitimate "odd structure" cases it must NOT regress.
/// </summary>
public class MidiSequencerDurationTests
{
    private const int Ppq = 480;

    private static MetaEvent Tempo120(long tick = 0) =>
        new() { Type = MetaEventType.SetTempo, Data = new byte[] { 0x07, 0xA1, 0x20 }, AbsoluteTicks = tick };

    private static MetaEvent EndOfTrack(long tick) =>
        new() { Type = MetaEventType.EndOfTrack, AbsoluteTicks = tick };

    private static T At<T>(T evt, long tick) where T : MidiEvent { evt.AbsoluteTicks = tick; return evt; }

    private static MidiSequencer Seq(params MidiTrack[] tracks)
    {
        var fmt = tracks.Length > 1 ? MidiFormat.MultiTrack : MidiFormat.SingleTrack;
        var header = new MidiHeader(fmt, (short)tracks.Length, TimeDivision.FromTicksPerQuarterNote(Ppq));
        return new MidiSequencer(new MidiFile(header, tracks));
    }

    [Fact]
    public void PaddedEndOfTrack_DurationAnchorsOnLastNote()
    {
        // Note ends at tick 960; EndOfTrack jammed out at tick 100000 (the bug pattern).
        var seq = Seq(new MidiTrack(new MidiEvent[]
        {
            Tempo120(),
            At(new NoteOnEvent { Channel = 0, Note = 60, Velocity = 100 }, 0),
            At(new NoteOffEvent { Channel = 0, Note = 60, Velocity = 0 }, 960),
            EndOfTrack(100000),
        }));

        Assert.Equal(seq.TickToTime(960), seq.Duration);
        Assert.True(seq.Duration < seq.TickToTime(100000));   // trailing silence trimmed
    }

    [Fact]
    public void HeldFinalNote_NotClipped()
    {
        // Last note-ON is early (tick 480) but it's held until note-OFF at tick 3840.
        // Duration must reach the note-off, never the note-on.
        var seq = Seq(new MidiTrack(new MidiEvent[]
        {
            Tempo120(),
            At(new NoteOnEvent { Channel = 0, Note = 60, Velocity = 100 }, 480),
            At(new NoteOffEvent { Channel = 0, Note = 60, Velocity = 0 }, 3840),
            EndOfTrack(50000),
        }));

        Assert.Equal(seq.TickToTime(3840), seq.Duration);
    }

    [Fact]
    public void NormalFile_EndOfTrackAtLastNote_Unchanged()
    {
        // The common, well-formed case: EndOfTrack sits at the same tick as the final note-off.
        // New and old behaviour must agree exactly — no change for normal files.
        var seq = Seq(new MidiTrack(new MidiEvent[]
        {
            Tempo120(),
            At(new NoteOnEvent { Channel = 0, Note = 60, Velocity = 100 }, 0),
            At(new NoteOffEvent { Channel = 0, Note = 60, Velocity = 0 }, 960),
            EndOfTrack(960),
        }));

        Assert.Equal(seq.TickToTime(960), seq.Duration);
    }

    [Fact]
    public void NotelessFile_FallsBackToFullLength()
    {
        // No notes anywhere (a tempo/automation/lyric-only track): nothing to anchor on, so the
        // nominal end (last event) is kept rather than collapsing to zero.
        var seq = Seq(new MidiTrack(new MidiEvent[]
        {
            Tempo120(),
            At(new ControlChangeEvent { Channel = 0, Controller = 7, Value = 100 }, 480),
            EndOfTrack(2000),
        }));

        Assert.Equal(seq.TickToTime(2000), seq.Duration);
    }

    [Fact]
    public void MultiTrack_PaddedConductor_AnchorsOnMusic()
    {
        // The Bohemian Rhapsody pattern: music track ends at tick 960; a separate conductor track's
        // EndOfTrack is padded to tick 262143. Duration follows the music, not the padded conductor.
        var conductor = new MidiTrack(new MidiEvent[] { Tempo120(), EndOfTrack(262143) });
        var music = new MidiTrack(new MidiEvent[]
        {
            At(new NoteOnEvent { Channel = 0, Note = 60, Velocity = 100 }, 0),
            At(new NoteOffEvent { Channel = 0, Note = 60, Velocity = 0 }, 960),
            EndOfTrack(960),
        });
        var seq = Seq(conductor, music);

        Assert.Equal(seq.TickToTime(960), seq.Duration);
        Assert.True(seq.Duration < seq.TickToTime(262143));
    }
}
