using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Loader;
using Loader.Sfz;
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
string? sfzReportTarget = null;
string? setupSpec = null;
var sampleRate = 48000;   // default = PipeWire/JACK graph rate; --rate overrides (WAV export / live engine rate)
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--patches": listPatches = true; break;
        case "--sfz-report":
            if (i + 1 >= args.Length) { Console.WriteLine("--sfz-report needs a .sfz file or a folder"); return 1; }
            sfzReportTarget = args[++i];
            break;
        case "--map":
            if (i + 1 >= args.Length) { Console.WriteLine("--map needs a spec, e.g. --map 30=Other.sf2"); return 1; }
            maps.Add(args[++i]);
            break;
        case "--setup":
            if (i + 1 >= args.Length) { Console.WriteLine("--setup needs a setup name, id, or .json file"); return 1; }
            setupSpec = args[++i];
            break;
        case "--rate":
            if (i + 1 >= args.Length || !int.TryParse(args[++i], out sampleRate) || sampleRate is < 8000 or > 384000)
            { Console.WriteLine("--rate needs a sample rate in Hz (8000-384000), e.g. --rate 44100"); return 1; }
            break;
        default: positionals.Add(args[i]); break;
    }
}

// Standalone: report which ARIA/SFZ opcodes a font (or a whole folder) uses that the loader
// drops. Parse-only, no audio — for triaging a collection. Returns before the playback path.
if (sfzReportTarget != null)
    return RunSfzReport(sfzReportTarget);

if (positionals.Count < 2 && setupSpec == null)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  Live playback:    MidiSharp.Demo <midi> <sf2>");
    Console.WriteLine("  Render to WAV:    MidiSharp.Demo <midi> <sf2> <out.wav> [--rate <hz>]   (default 48000; e.g. --rate 44100)");
    Console.WriteLine("  GM SFZ + drums:   pass \"melodic.sfz+drums.sfz\" as <sf2> (1st→bank 0, rest→bank 128),");
    Console.WriteLine("                    or just the \"... Melodic ....sfz\" file and its \"Drums\" sibling auto-pairs.");
    Console.WriteLine();
    Console.WriteLine("  Play a setup:     MidiSharp.Demo --setup <name|id|file.json> [out.wav] [--rate <hz>]");
    Console.WriteLine("                      a saved web-player setup (MIDI + base font + instrument substitutions);");
    Console.WriteLine($"                      resolved by name/id under {SetupSupport.DefaultRoot}, or a direct .json path.");
    Console.WriteLine();
    Console.WriteLine("  List patches:     MidiSharp.Demo --patches <midi> <sf2>");
    Console.WriteLine("  SFZ opcode report: MidiSharp.Demo --sfz-report <font.sfz | folder>   (which ARIA opcodes are dropped)");
    Console.WriteLine("  Override patches: MidiSharp.Demo <midi> <sf2> [out.wav] --map <prog>=<font>[:<srcProg>] [--map ...]");
    Console.WriteLine("                      --map 30=OtherGM.sf2        program 30 ← OtherGM's program 30 (GM-aligned)");
    Console.WriteLine("                      --map 30=Guitars.sf2:5      program 30 ← Guitars' program 5");
    Console.WriteLine("                      --map 128:0=OtherDrums.sf2  swap the whole drum kit (bank 128, prog 0)");
    return 1;
}

// A setup supplies its own MIDI + base font; any positional left over is the render target (out.wav).
SetupFile? setup = null;
string midiPath, sf2Path;
string? renderPath;
if (setupSpec != null)
{
    setup = SetupSupport.Resolve(setupSpec, out var setupError);
    if (setup == null) { Console.WriteLine(setupError); return 1; }
    midiPath = setup.midiPath;
    sf2Path = setup.soundfontPath;
    renderPath = positionals.Count >= 1 ? positionals[0] : null;
    Console.WriteLine($"Setup: {setup.name}  ({setup.overrides?.Length ?? 0} patch + " +
                      $"{setup.trackOverrides?.Length ?? 0} track override(s))");
}
else
{
    midiPath = positionals[0];
    sf2Path = positionals[1];
    renderPath = positionals.Count >= 3 ? positionals[2] : null;
}

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
// Offline render has no real-time deadline, so decode samples synchronously on first use — otherwise
// fast first-hit notes out-run the lazy background decoder and lose their attack (non-deterministic
// "dropped notes"). Live playback keeps the default lazy path (must never block the audio thread).
var loadOptions = new SoundBankLoadOptions { BlockingSampleDecode = renderPath != null };
var soundBank = LoadBank(sf2Path, loadOptions);
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

// Build the bank the synth plays: a saved setup's overrides, or --map patch swaps, or the base font
// as-is. The synth consumes one bank — it can't tell a composite from a native font. A setup may also
// carry per-track routing, returned as a track→patch map handed to Synthesizer.SetTrackPatchMap.
var playBank = soundBank;
PatchMapSession? session = null;
IReadOnlyDictionary<int, (int Bank, int Program)>? trackPatchMap = null;
if (setup != null && ((setup.overrides?.Length ?? 0) > 0 || (setup.trackOverrides?.Length ?? 0) > 0))
{
    try
    {
        session = SetupSupport.BuildSession(setup, soundBank, loadOptions);
        var resolved = session.BuildResolved();
        playBank = resolved.Bank;
        trackPatchMap = resolved.TrackPatchMap;
        foreach (var o in setup.overrides ?? [])
            Console.WriteLine($"  override prog {o.logicalProgram} → {Path.GetFileName(o.sourcePath)} " +
                              $"(bank {o.sourceBank}, prog {o.sourceProgram})");
        foreach (var o in setup.trackOverrides ?? [])
            Console.WriteLine($"  track {o.trackIndex} ({o.trackName ?? "?"}) → {Path.GetFileName(o.sourcePath)} " +
                              $"(bank {o.sourceBank}, prog {o.sourceProgram})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Setup error: {ex.Message}");
        session?.Dispose();
        return 1;
    }
}
else if (maps.Count > 0)
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
                src = SoundBankLoader.Load(font, loadOptions);
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

// sampleRate comes from --rate (default 48000). Live at 48000 = native JACK client (no resample);
// for WAV export any rate works (e.g. --rate 44100 for CD-rate files).
var synth = new Synthesizer(sampleRate);
synth.LoadSoundFont(playBank);
if (trackPatchMap != null) synth.SetTrackPatchMap(trackPatchMap);

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
// Scan one .sfz or a folder of them and print which opcodes/headers the loader drops.
static int RunSfzReport(string target)
{
    var files = new List<string>();
    if (Directory.Exists(target))
        files.AddRange(Directory.EnumerateFiles(target, "*.sfz", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    else if (File.Exists(target))
        files.Add(target);
    else { Console.WriteLine($"Not found: {target}"); return 1; }

    if (files.Count == 0) { Console.WriteLine($"No .sfz files under {target}"); return 0; }
    bool single = files.Count == 1;

    // family -> (#fonts using it, total regions across fonts, note)
    var rollup = new Dictionary<string, (int Fonts, int Regions, string? Note)>(StringComparer.Ordinal);
    int withFindings = 0;

    foreach (var f in files)
    {
        SfzLoadReport rep;
        try { rep = SfzDiagnostics.Scan(f); }
        catch (Exception ex) { Console.WriteLine($"\n{Path.GetFileName(f)}: scan failed — {ex.Message}"); continue; }

        if (rep.HasFindings) withFindings++;

        if (single || rep.HasFindings)
        {
            Console.WriteLine();
            Console.WriteLine($"{rep.Name}  ({rep.RegionCount} region{(rep.RegionCount == 1 ? "" : "s")})");
            if (rep.IgnoredHeaders.Count > 0)
                Console.WriteLine("  ignored headers: " +
                    string.Join(", ", rep.IgnoredHeaders.Select(h => $"<{h.Header}>×{h.Count}")));
            foreach (var op in rep.UnsupportedOpcodes)
                Console.WriteLine($"  {op.Opcode,-24}{op.Count,6}  {op.Note}");
            if (!rep.HasFindings)
                Console.WriteLine("  fully supported");
        }

        foreach (var op in rep.UnsupportedOpcodes)
        {
            rollup.TryGetValue(op.Opcode, out var agg);
            rollup[op.Opcode] = (agg.Fonts + 1, agg.Regions + op.Count, op.Note);
        }
    }

    if (!single)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Rollup: {files.Count} fonts scanned, {withFindings} use unsupported opcodes ===");
        Console.WriteLine($"  {"opcode",-24}{"fonts",6}{"regions",9}  note");
        foreach (var kv in rollup.OrderByDescending(k => k.Value.Fonts).ThenByDescending(k => k.Value.Regions)
                                  .ThenBy(k => k.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {kv.Key,-24}{kv.Value.Fonts,6}{kv.Value.Regions,9}  {kv.Value.Note}");
    }
    return 0;
}

static IRBank LoadBank(string spec, SoundBankLoadOptions options)
{
    if (spec.Contains('+'))
    {
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = new List<(string, int)>();
        for (int i = 0; i < parts.Length; i++) files.Add((parts[i], i == 0 ? 0 : 128));
        return SoundBankLoader.LoadSfz(files, options);
    }

    if (spec.EndsWith(".sfz", StringComparison.OrdinalIgnoreCase) &&
        spec.Contains("Melodic", StringComparison.OrdinalIgnoreCase))
    {
        int i = spec.IndexOf("Melodic", StringComparison.OrdinalIgnoreCase);
        string drums = spec[..i] + "Drums" + spec[(i + "Melodic".Length)..];
        if (File.Exists(drums))
        {
            Console.WriteLine($"  (auto-pairing percussion: {Path.GetFileName(drums)} → bank 128)");
            return SoundBankLoader.LoadSfz(new[] { (spec, 0), (drums, 128) }, options);
        }
    }

    return SoundBankLoader.Load(spec, options);
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
