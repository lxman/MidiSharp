using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MidiSharp.IO;
using MidiSharp.Model.Events;
using MidiSharp.Synth;
using MidiSharp.Synth.OwnAudio;
using SF2.Net;

if (args.Length < 2)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  Live playback:    MidiSharp.Demo <midi> <sf2>");
    Console.WriteLine("  Render to WAV:    MidiSharp.Demo <midi> <sf2> <out.wav>");
    return 1;
}

var midiPath = args[0];
var sf2Path = args[1];
var renderPath = args.Length >= 3 ? args[2] : null;

if (!File.Exists(midiPath)) { Console.WriteLine($"MIDI not found: {midiPath}"); return 1; }
if (!File.Exists(sf2Path)) { Console.WriteLine($"SF2 not found: {sf2Path}"); return 1; }

Console.WriteLine($"Loading MIDI: {midiPath}");
var midiFile = MidiFileReader.Read(File.ReadAllBytes(midiPath));
Console.WriteLine($"  Format: {midiFile.Header.Format}, Tracks: {midiFile.Header.TrackCount}, " +
                  $"Division: {midiFile.Header.Division.TicksPerQuarterNote} ticks/quarter");

Console.WriteLine($"Loading SoundFont: {sf2Path}");
var soundFont = SoundFont.Load(sf2Path);
Console.WriteLine($"  {soundFont.Info.BankName}: {soundFont.Presets.Count} presets, " +
                  $"{soundFont.Instruments.Count} instruments, {soundFont.SampleHeaders.Count} samples");

const int sampleRate = 44100;
var synth = new Synthesizer(sampleRate);
synth.LoadSoundFont(soundFont);

// One player, two modes — the same RealtimePlayer drives both live audio and
// offline rendering. The render path pulls blocks synchronously in a loop;
// the live path lets the audio backend pull blocks via its callback.
var player = new RealtimePlayer(midiFile, synth);

if (renderPath != null)
    return RenderToWav(player, renderPath, sampleRate);

return PlayLive(player, sampleRate);

static int PlayLive(RealtimePlayer player, int sampleRate)
{
    using var audio = new OwnAudioOutput(sampleRate, channels: 2);
    audio.SetCallback((buffer, frames) => player.ProcessBlockInterleaved(buffer.AsSpan(0, frames * 2)));
    audio.Start();

    Console.WriteLine($"Duration: {player.Duration:mm\\:ss\\.fff}");
    Console.WriteLine("Controls:  [Space] play/pause   [S] stop/reset   [Q] quit");
    Console.WriteLine();

    var interactive = !Console.IsInputRedirected && Environment.UserInteractive;
    var running = true;
    while (running && (interactive || !player.IsFinished))
    {
        if (interactive)
        {
            var state = player.IsPaused ? "Paused " : player.IsFinished ? "Done   " : "Playing";
            Console.Write($"\r[{state}] {player.Position:mm\\:ss\\.fff} / {player.Duration:mm\\:ss\\.fff}  " +
                          $"voices={player.Synthesizer.ActiveVoiceCount,3}   ");

            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Spacebar:
                        if (player.IsPaused) player.Resume();
                        else player.Pause();
                        break;
                    case ConsoleKey.S: player.Reset(); break;
                    case ConsoleKey.Q: running = false; break;
                }
            }
        }
        Thread.Sleep(interactive ? 50 : 100);
    }

    audio.Stop();
    Console.WriteLine("\nGoodbye!");
    return 0;
}

static int RenderToWav(RealtimePlayer player, string outPath, int sampleRate)
{
    // Render the sequence plus a 2-second tail so envelope releases finish cleanly.
    var totalFrames = (long)((player.Duration.TotalSeconds + 2.0) * sampleRate);
    var totalEvents = player.Sequencer.Events.Count;
    var totalPitchBends = 0;
    foreach (var e in player.Sequencer.Events)
        if (e.Event is PitchBendEvent) totalPitchBends++;

    Console.WriteLine($"Rendering to: {outPath}");
    Console.WriteLine($"  Duration: {player.Duration:mm\\:ss\\.fff} + 2s tail = {totalFrames} frames");

    using var fs = File.Create(outPath);
    WriteWavHeader(fs, sampleRate, channels: 2, totalFrames);

    const int blockFrames = 1024;
    var left = new float[blockFrames];
    var right = new float[blockFrames];
    var pcm = new byte[blockFrames * 4]; // stereo int16
    long renderedFrames = 0;
    var sw = Stopwatch.StartNew();

    while (renderedFrames < totalFrames)
    {
        var n = (int)Math.Min(blockFrames, totalFrames - renderedFrames);
        player.ProcessBlock(left.AsSpan(0, n), right.AsSpan(0, n));

        for (var i = 0; i < n; i++)
        {
            var l = (short)Math.Clamp(left[i] * 32767f, -32768f, 32767f);
            var r = (short)Math.Clamp(right[i] * 32767f, -32768f, 32767f);
            pcm[i * 4 + 0] = (byte)(l & 0xFF);
            pcm[i * 4 + 1] = (byte)((l >> 8) & 0xFF);
            pcm[i * 4 + 2] = (byte)(r & 0xFF);
            pcm[i * 4 + 3] = (byte)((r >> 8) & 0xFF);
        }
        fs.Write(pcm, 0, n * 4);

        renderedFrames += n;
        if (renderedFrames % (sampleRate * 5) == 0 || renderedFrames == totalFrames)
        {
            var pct = 100.0 * renderedFrames / totalFrames;
            Console.Write($"\r  {pct,5:F1}%  voices={player.Synthesizer.ActiveVoiceCount,3}   ");
        }
    }

    var pbDispatched = 0;
    foreach (var e in player.Sequencer.Events)
        if (e.Event is PitchBendEvent) pbDispatched++;

    Console.WriteLine($"\n  Done in {sw.Elapsed.TotalSeconds:F2}s " +
                      $"({player.Duration.TotalSeconds / sw.Elapsed.TotalSeconds:F1}x real-time)" +
                      $"  peakVoices={player.PeakActiveVoices}");
    Console.WriteLine($"  events: {player.EventsDispatched}/{totalEvents} dispatched; " +
                      $"pitchBends: {pbDispatched}/{totalPitchBends}");
    return 0;
}

static void WriteWavHeader(Stream s, int sampleRate, int channels, long frames)
{
    var byteRate = sampleRate * channels * 2;
    var dataSize = (int)(frames * channels * 2);
    var fileSize = 36 + dataSize;
    var bw = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true);
    bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
    bw.Write(fileSize);
    bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
    bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
    bw.Write(16);                          // fmt chunk size
    bw.Write((short)1);                    // PCM
    bw.Write((short)channels);
    bw.Write(sampleRate);
    bw.Write(byteRate);
    bw.Write((short)(channels * 2));       // block align
    bw.Write((short)16);                   // bits per sample
    bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
    bw.Write(dataSize);
    bw.Flush();
}
