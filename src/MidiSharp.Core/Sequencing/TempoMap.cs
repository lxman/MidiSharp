using System;
using System.Collections.Generic;
using MidiSharp.Model;
using MidiSharp.Model.Events;

namespace MidiSharp.Sequencing;

/// <summary>
/// Tracks tempo changes throughout a MIDI file.
/// Used to convert between tick time and real time.
/// </summary>
public sealed class TempoMap
{
    private readonly List<TempoChange> _tempoChanges;
    private readonly int _ticksPerQuarterNote;

    /// <summary>
    /// Default tempo: 120 BPM = 500,000 microseconds per quarter note.
    /// </summary>
    public const int DefaultMicrosecondsPerBeat = 500_000;

    private TempoMap(List<TempoChange> tempoChanges, int ticksPerQuarterNote)
    {
        _tempoChanges = tempoChanges;
        _ticksPerQuarterNote = ticksPerQuarterNote;
    }

    /// <summary>
    /// The ticks per quarter note from the MIDI header.
    /// </summary>
    public int TicksPerQuarterNote => _ticksPerQuarterNote;

    /// <summary>
    /// The tempo changes in this map.
    /// </summary>
    public IReadOnlyList<TempoChange> TempoChanges => _tempoChanges;

    /// <summary>
    /// Builds a tempo map from a MIDI file.
    /// </summary>
    public static TempoMap BuildFrom(MidiFile file)
    {
        if (file.Header.Division.IsSmpte)
        {
            throw new NotSupportedException("SMPTE time division not yet supported");
        }

        int ticksPerQuarterNote = file.Header.Division.TicksPerQuarterNote;
        var tempoChanges = new List<TempoChange>();

        // For Format 1 files, tempo events are in the first track
        // For Format 0 files, they're mixed with other events
        // We scan all tracks to be safe
        foreach (MidiTrack? track in file.Tracks)
        {
            foreach (MidiEvent? evt in track.Events)
            {
                if (evt is MetaEvent { Type: MetaEventType.SetTempo, Tempo: not null } meta)
                {
                    tempoChanges.Add(new TempoChange(evt.AbsoluteTicks, meta.Tempo.Value));
                }
            }
        }

        // Sort by tick position
        tempoChanges.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        // Ensure there's always a tempo at tick 0
        if (tempoChanges.Count == 0 || tempoChanges[0].Tick > 0)
        {
            tempoChanges.Insert(0, new TempoChange(0, DefaultMicrosecondsPerBeat));
        }

        // Calculate real-time positions for each tempo change
        CalculateRealTimes(tempoChanges, ticksPerQuarterNote);

        return new TempoMap(tempoChanges, ticksPerQuarterNote);
    }

    private static void CalculateRealTimes(List<TempoChange> changes, int ticksPerQuarterNote)
    {
        double totalMicroseconds = 0;
        long previousTick = 0;
        int previousTempo = DefaultMicrosecondsPerBeat;

        for (var i = 0; i < changes.Count; i++)
        {
            TempoChange change = changes[i];

            // Calculate time from previous point to this one
            long deltaTicks = change.Tick - previousTick;
            double deltaTime = (double)deltaTicks * previousTempo / ticksPerQuarterNote;
            totalMicroseconds += deltaTime;

            // Update the change with calculated real time
            changes[i] = new TempoChange(change.Tick, change.MicrosecondsPerBeat, totalMicroseconds);

            previousTick = change.Tick;
            previousTempo = change.MicrosecondsPerBeat;
        }
    }

    /// <summary>
    /// Gets the tempo (microseconds per beat) at a given tick position.
    /// </summary>
    public int GetTempoAtTick(long tick)
    {
        // Binary search for the tempo change at or before this tick
        int index = FindTempoChangeIndex(tick);
        return _tempoChanges[index].MicrosecondsPerBeat;
    }

    /// <summary>
    /// Gets the BPM at a given tick position.
    /// </summary>
    public double GetBpmAtTick(long tick)
    {
        int microsecondsPerBeat = GetTempoAtTick(tick);
        return 60_000_000.0 / microsecondsPerBeat;
    }

    /// <summary>
    /// Converts a tick position to real time.
    /// </summary>
    public TimeSpan TickToTime(long tick)
    {
        int index = FindTempoChangeIndex(tick);
        TempoChange tempoChange = _tempoChanges[index];

        // Calculate time from the tempo change point to the target tick
        long deltaTicks = tick - tempoChange.Tick;
        double deltaMicroseconds = (double)deltaTicks * tempoChange.MicrosecondsPerBeat / _ticksPerQuarterNote;
        double totalMicroseconds = tempoChange.RealTimeMicroseconds + deltaMicroseconds;

        return TimeSpan.FromTicks((long)(totalMicroseconds * 10)); // 1 tick = 0.1 microseconds
    }

    /// <summary>
    /// Converts real time to a tick position.
    /// </summary>
    public long TimeToTick(TimeSpan time)
    {
        double targetMicroseconds = time.Ticks / 10.0;

        // Find the tempo change at or before this time
        var index = 0;
        for (int i = _tempoChanges.Count - 1; i >= 0; i--)
        {
            if (_tempoChanges[i].RealTimeMicroseconds <= targetMicroseconds)
            {
                index = i;
                break;
            }
        }

        TempoChange tempoChange = _tempoChanges[index];
        double deltaMicroseconds = targetMicroseconds - tempoChange.RealTimeMicroseconds;
        var deltaTicks = (long)(deltaMicroseconds * _ticksPerQuarterNote / tempoChange.MicrosecondsPerBeat);

        return tempoChange.Tick + deltaTicks;
    }

    private int FindTempoChangeIndex(long tick)
    {
        // Binary search for the last tempo change at or before the given tick
        var low = 0;
        int high = _tempoChanges.Count - 1;
        var result = 0;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (_tempoChanges[mid].Tick <= tick)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }
}

/// <summary>
/// Represents a tempo change at a specific tick position.
/// </summary>
public readonly struct TempoChange(long tick, int microsecondsPerBeat, double realTimeMicroseconds = 0)
{
    /// <summary>
    /// The tick position of this tempo change.
    /// </summary>
    public long Tick { get; } = tick;

    /// <summary>
    /// Microseconds per quarter note (beat).
    /// </summary>
    public int MicrosecondsPerBeat { get; } = microsecondsPerBeat;

    /// <summary>
    /// The real-time position in microseconds (calculated).
    /// </summary>
    public double RealTimeMicroseconds { get; } = realTimeMicroseconds;

    /// <summary>
    /// Beats per minute.
    /// </summary>
    public double Bpm => 60_000_000.0 / MicrosecondsPerBeat;

    public override string ToString() => $"Tick {Tick}: {Bpm:F1} BPM";
}
