using System;
using System.Linq;
using MidiSharp.Hosting.Clap;
using MidiSharp.Model;
using MidiSharp.Model.Events;
using MidiSharp.Synth;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Phase-4b gate: a MIDI file plays through a hosted CLAP instrument routed into the audio pipeline. A
/// programmatic MIDI (an A4 held on channel 0) drives a <see cref="RealtimePlayer"/>; its
/// <see cref="RealtimePlayer.EventDispatched"/> hook routes the channel's notes — sample-accurately — to a
/// hosted instrument whose output is summed into the mix, while the synth's own copy of the channel is
/// muted. The rendered result is measured: it must carry the instrument's tone at the note's pitch.
/// Self-skips without the synth fixture.
/// </summary>
public sealed class ClapPlayerIntegrationTests
{
    private const int Rate = 48000;
    private static readonly AudioConfig Config = new(Rate, 512, ChannelCount: 2);

    private static MidiFile HeldNote(int channel, int key, int ticks)
    {
        var header = new MidiHeader(MidiFormat.SingleTrack, 1, TimeDivision.FromTicksPerQuarterNote(480));
        var track = new MidiTrack([
            new MetaEvent { Type = MetaEventType.SetTempo, Data = new byte[] { 0x07, 0xA1, 0x20 }, AbsoluteTicks = 0 }, // 120 bpm
            new NoteOnEvent { Channel = (byte)channel, Note = (byte)key, Velocity = 100, AbsoluteTicks = 0 },
            new NoteOffEvent { Channel = (byte)channel, Note = (byte)key, Velocity = 0, AbsoluteTicks = ticks },
            new MetaEvent { Type = MetaEventType.EndOfTrack, AbsoluteTicks = ticks },
        ]);
        return new MidiFile(header, [track]);
    }

    [Fact]
    public void Midi_file_plays_through_a_hosted_clap_instrument()
    {
        PluginDescriptor? d = new ClapFormat().Scan(new ClapFormat().DefaultSearchPaths)
            .FirstOrDefault(p => p.Id == "midisharp.test.synth");
        Assert.SkipWhen(d == null, "synth fixture not installed.");

        var synth = new Synthesizer(Rate);                 // no SoundFont loaded → silent on its own
        using var inst = new HostedInstrument(new ClapFormat().Load(d!, Config), Config);
        var player = new RealtimePlayer(HeldNote(channel: 0, key: 69, ticks: 1920), synth);   // A4 for ~2 s

        synth.MuteChannel(0, true);                        // the hosted instrument owns channel 0
        player.EventDispatched += (se, offset) =>
        {
            switch (se.Event)
            {
                case NoteOnEvent e when e.Channel == 0:
                    if (e.Velocity == 0) inst.NoteOff(offset, 0, e.Note);
                    else inst.NoteOn(offset, 0, e.Note, e.Velocity);
                    break;
                case NoteOffEvent e when e.Channel == 0:
                    inst.NoteOff(offset, 0, e.Note);
                    break;
            }
        };

        // Render ~1.8 s (well within the held note) summing synth + instrument, like the live callback.
        const int blocks = 170;   // 170 * 512 ≈ 1.81 s
        var mix = new float[512 * 2];
        var instBuf = new float[512 * 2];
        var left = new float[blocks * 512];
        for (var b = 0; b < blocks; b++)
        {
            Array.Clear(mix);
            player.ProcessBlockInterleaved(mix);           // synth (silent) + fires EventDispatched → queues notes
            inst.Render(instBuf);                          // hosted instrument renders the queued notes
            for (var i = 0; i < mix.Length; i++) mix[i] += instBuf[i];
            for (var i = 0; i < 512; i++) left[b * 512 + i] = mix[2 * i];
        }

        double rms = 0; var crossings = 0;
        for (var i = 0; i < left.Length; i++) rms += (double)left[i] * left[i];
        rms = Math.Sqrt(rms / left.Length);
        for (var i = 1; i < left.Length; i++)
            if ((left[i - 1] < 0f && left[i] >= 0f) || (left[i - 1] >= 0f && left[i] < 0f)) crossings++;
        double hz = crossings * (double)Rate / (2.0 * left.Length);

        Assert.True(rms > 0.1, $"the hosted instrument should sound through the player (rms {rms:F4}).");
        Assert.True(Math.Abs(hz - 440.0) < 5.0, $"channel-0 notes should sound at A4 ~440 Hz via the plugin (measured {hz:F1}).");
    }
}
