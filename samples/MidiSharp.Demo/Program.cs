using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MidiSharp.IO;
using MidiSharp.Model.Events;
using MidiSharp.PatchMap;
using MidiSharp.SoundBank;
using MidiSharp.Synth;
using MidiSharp.Synth.OwnAudio;
using IRBank = MidiSharp.SoundBank.SoundBank;

// Flags may appear anywhere; everything else is positional (<midi> <sf2> [out.wav]).
var positionals = new List<string>();
var maps = new List<string>();
var listPatches = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--patches": listPatches = true; break;
        case "--map":
            if (i + 1 >= args.Length) { Console.WriteLine("--map needs a spec, e.g. --map 30=Other.sf2"); return 1; }
            maps.Add(args[++i]);
            break;
        default: positionals.Add(args[i]); break;
    }
}

if (positionals.Count < 2)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  Live playback:    MidiSharp.Demo <midi> <sf2>");
    Console.WriteLine("  Render to WAV:    MidiSharp.Demo <midi> <sf2> <out.wav>");
    Console.WriteLine("  GM SFZ + drums:   pass \"melodic.sfz+drums.sfz\" as <sf2> (1st→bank 0, rest→bank 128),");
    Console.WriteLine("                    or just the \"... Melodic ....sfz\" file and its \"Drums\" sibling auto-pairs.");
    Console.WriteLine();
    Console.WriteLine("  List patches:     MidiSharp.Demo --patches <midi> <sf2>");
    Console.WriteLine("  Override patches: MidiSharp.Demo <midi> <sf2> [out.wav] --map <prog>=<font>[:<srcProg>] [--map ...]");
    Console.WriteLine("                      --map 30=OtherGM.sf2        program 30 ← OtherGM's program 30 (GM-aligned)");
    Console.WriteLine("                      --map 30=Guitars.sf2:5      program 30 ← Guitars' program 5");
    Console.WriteLine("                      --map 128:0=OtherDrums.sf2  swap the whole drum kit (bank 128, prog 0)");
    return 1;
}

var midiPath = positionals[0];
var sf2Path = positionals[1];
var renderPath = positionals.Count >= 3 ? positionals[2] : null;

if (!File.Exists(midiPath)) { Console.WriteLine($"MIDI not found: {midiPath}"); return 1; }
if (!sf2Path.Contains('+') && !File.Exists(sf2Path)) { Console.WriteLine($"SoundFont not found: {sf2Path}"); return 1; }

Console.WriteLine($"Loading MIDI: {midiPath}");
var repair = SmfRepairFilter.Scan(File.ReadAllBytes(midiPath));
if (repair.HasDefects)
{
    Console.WriteLine($"  Repair: {repair.CorrectedCount} corrected of {repair.Defects.Count} defect(s):");
    foreach (var d in repair.Defects)
        Console.WriteLine($"    {d}");
}
var midiFile = MidiFileReader.Read(repair.Data);
Console.WriteLine($"  Format: {midiFile.Header.Format}, Tracks: {midiFile.Tracks.Count}, " +
                  $"Division: {midiFile.Header.Division.TicksPerQuarterNote} ticks/quarter");

Console.WriteLine($"Loading SoundFont: {sf2Path}");
var soundBank = LoadBank(sf2Path);
Console.WriteLine($"  {soundBank.Name}: {soundBank.Patches.Count} patches, " +
                  $"{soundBank.Samples.Count} samples");

// --patches: report what the song would normally play (resolved against the base font), then exit.
if (listPatches)
{
    Console.WriteLine("Patches used by this song:");
    foreach (var u in PatchUsageAnalyzer.Analyze(midiFile, soundBank))
    {
        var addr = u.IsDrum
            ? $"kit (bank 128) prog {u.Program,3}"
            : $"prog {u.Program,3}" + (u.Bank != 0 ? $" bank {u.Bank}" : "");
        Console.WriteLine($"  {addr}  ch[{string.Join(",", u.Channels)}]  {u.BaseName ?? "(not in base font)"}");
    }
    return 0;
}

// --map: build a composite that substitutes chosen patches with instruments from other fonts.
// The synth still consumes one bank — it can't tell the composite from a native font.
var playBank = soundBank;
PatchMapSession? session = null;
if (maps.Count > 0)
{
    try
    {
        session = new PatchMapSession(soundBank);
        var srcByPath = new Dictionary<string, IRBank>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in maps)
        {
            var (lb, lp, font, sb, sp) = ParseMap(spec);
            if (!File.Exists(font)) { Console.WriteLine($"Override font not found: {font}"); session.Dispose(); return 1; }
            if (!srcByPath.TryGetValue(font, out var src))
            {
                src = SoundBankLoader.Load(font);
                session.AddSource(src);
                srcByPath[font] = src;
            }
            session.SetOverride(lb, lp, new PatchRef(src, sb, sp));
            var what = lb == 128 ? $"kit prog {lp}" : $"prog {lp}" + (lb != 0 ? $" bank {lb}" : "");
            Console.WriteLine($"  override {what} → {Path.GetFileName(font)} (bank {sb}, prog {sp})");
        }
        playBank = session.BuildComposite();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Override error: {ex.Message}");
        session?.Dispose();
        return 1;
    }
}

const int sampleRate = 44100;
var synth = new Synthesizer(sampleRate);
synth.LoadSoundFont(playBank);

// One player, two modes — the same RealtimePlayer drives both live audio and
// offline rendering. The render path pulls blocks synchronously in a loop;
// the live path lets the audio backend pull blocks via its callback.
var player = new RealtimePlayer(midiFile, synth);

var exitCode = renderPath != null
    ? RenderToWav(player, renderPath, sampleRate)
    : PlayLive(player, sampleRate);
session?.Dispose();   // releases the base + override source fonts (no-op if no overrides)
return exitCode;

// Loads a sound bank. Plain path → single bank. For GM SFZ banks split into
// melodic + percussion files, either pass "melodic.sfz+drums.sfz" (1st on bank 0,
// the rest on bank 128 where the synth routes channel 10), or pass a "... Melodic
// ....sfz" file and let its "Drums" sibling auto-pair.
static IRBank LoadBank(string spec)
{
    if (spec.Contains('+'))
    {
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = new List<(string, int)>();
        for (int i = 0; i < parts.Length; i++) files.Add((parts[i], i == 0 ? 0 : 128));
        return SoundBankLoader.LoadSfz(files);
    }

    if (spec.EndsWith(".sfz", StringComparison.OrdinalIgnoreCase) &&
        spec.Contains("Melodic", StringComparison.OrdinalIgnoreCase))
    {
        int i = spec.IndexOf("Melodic", StringComparison.OrdinalIgnoreCase);
        string drums = spec[..i] + "Drums" + spec[(i + "Melodic".Length)..];
        if (File.Exists(drums))
        {
            Console.WriteLine($"  (auto-pairing percussion: {Path.GetFileName(drums)} → bank 128)");
            return SoundBankLoader.LoadSfz(new[] { (spec, 0), (drums, 128) });
        }
    }

    return SoundBankLoader.Load(spec);
}

// Parse a --map spec: "<prog>=<font>[:<srcProg>]" or "<bank>:<prog>=<font>[:<srcBank>:<srcProg>]".
// Source bank/program default to the logical bank/program (GM-aligned), so "30=Other.sf2" means
// "program 30 from Other.sf2", and "128:0=Drums.sf2" swaps the whole kit. Trailing :N or :N:N on
// the right are the source coordinates; everything before them is the font path.
static (int lb, int lp, string font, int sb, int sp) ParseMap(string spec)
{
    var eq = spec.IndexOf('=');
    if (eq <= 0)
        throw new FormatException($"Bad --map '{spec}'. Use <prog>=<font> or <bank>:<prog>=<font>:<srcBank>:<srcProg>.");

    var left = spec[..eq];
    var right = spec[(eq + 1)..];

    int lb = 0, lp;
    var lparts = left.Split(':');
    if (lparts.Length == 1) lp = int.Parse(lparts[0]);
    else { lb = int.Parse(lparts[0]); lp = int.Parse(lparts[1]); }

    // Peel up to two trailing integer tokens off the right as the source coordinates;
    // the remainder is the font path (so a path may itself contain colons).
    var rparts = right.Split(':');
    var tail = new List<int>();
    var idx = rparts.Length - 1;
    while (idx > 0 && tail.Count < 2 && int.TryParse(rparts[idx], out var n)) { tail.Insert(0, n); idx--; }
    var font = string.Join(":", rparts, 0, idx + 1);

    int sb = lb, sp = lp;
    if (tail.Count == 1) sp = tail[0];
    else if (tail.Count == 2) { sb = tail[0]; sp = tail[1]; }

    if (string.IsNullOrWhiteSpace(font))
        throw new FormatException($"Bad --map '{spec}': missing font path.");
    return (lb, lp, font, sb, sp);
}

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
