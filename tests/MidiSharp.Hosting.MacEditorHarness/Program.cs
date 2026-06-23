using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using MidiSharp.Hosting.AudioUnit;
using MidiSharp.Hosting.Clap;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.MacEditorHarness;
using MidiSharp.Hosting.Vst2;
using MidiSharp.Hosting.Vst3;

// Runs on the process main thread (thread 0), which AppKit requires and xUnit cannot provide. Verifies the
// Cocoa editor backend end-to-end: a managed fake editor (always), the clean-room VST2 fixture, and VST3 + CLAP
// against a real installed plugin (Surge XT) discovered through the format's own search paths. Each check prints
// PASS/FAIL/SKIP; exit 0 unless something present FAILED.

if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP: Cocoa editor backend is macOS-only."); return 0; }
if (!EditorPlatform.Current.IsAvailable) { Console.WriteLine("SKIP: no window server (headless session)."); return 0; }

var failures = 0;

// ── Backend gate: a managed fake editor that adds a child NSView (no native plugin) ──
{
    var gui = new FakeCocoaEditorGui(320, 240);
    using EditorSession? session = EditorSession.Open(gui, "MidiSharp macOS editor harness");
    if (session is not { IsOpen: true })
    {
        Console.WriteLine($"FAIL fake: editor session did not open (error: {session?.Error}).");
        failures++;
    }
    else
    {
        uint children = 0;
        for (var i = 0; i < 40 && children == 0; i++) { children = session.EmbeddedChildCount; if (children == 0) session.PumpOnce(25); }
        session.Close();
        if (children < 1) { Console.WriteLine("FAIL fake: no child NSView embedded."); failures++; }
        else Console.WriteLine($"PASS fake: embedded {children} child NSView(s).");
    }
}

// ── VST2: clean-room fixture (.so); ── VST3 + CLAP: a real installed plugin (Surge XT) ──
failures += EmbedFixture("VST2", new Vst2Format(), "midisharp_gui_vst2.so", "MidiSharp VST2 Gain", "VST2 cocoa editor");
failures += EmbedReal("VST3", new Vst3Format(), "Surge XT", "VST3 cocoa editor");
failures += EmbedReal("CLAP", new ClapFormat(), "Surge XT", "CLAP cocoa editor");
failures += EmbedAu("AU", new AudioUnitFormat(), "aufx:lpas", "AU cocoa editor (AULowpass)");

// AU v3 (the AUAudioUnit front-end, AudioUnitV3Plugin) — production routing sends async/v3 components here. Also a
// main-thread proof: instantiation + editor land on the main dispatch queue, which xUnit's pool threads can't
// drain. An effect (OOP), an instrument (in-process), and the plugin's own custom editor.
failures += RenderAuV3("AU v3 effect", "aufx:DIMC", isInstrument: false);
failures += RenderAuV3("AU v3 instrument", "aumu:audM", isInstrument: true);
failures += EmbedAu("AU v3 editor", new AudioUnitFormat(), "aufx:DIMC", "AU v3 custom editor (DimChorus)");

return failures == 0 ? 0 : 1;

// AU discovery is registry-based (no file path), so find the unit by its type:subtype id prefix via Scan, then
// embed its Cocoa view (custom kAudioUnitProperty_CocoaUI view, or the generic fallback) through DoEmbed.
static int EmbedAu(string label, AudioUnitFormat format, string idPrefix, string title)
{
    PluginDescriptor? desc = null;
    foreach (PluginDescriptor d in format.Scan(format.DefaultSearchPaths))
        if (d.Id.StartsWith(idPrefix, StringComparison.Ordinal)) { desc = d; break; }
    if (desc == null) { Console.WriteLine($"SKIP {label}: no AU matching '{idPrefix}'."); return 0; }
    return DoEmbed(label, format, desc, title);
}

// Resolve a fixture from the built-fixtures dir by exact name, then embed it.
static int EmbedFixture(string label, IPluginFormat format, string fixtureFile, string pluginName, string title)
{
    if (!MacFixtures.Has(fixtureFile)) { Console.WriteLine($"SKIP {label}: fixture {fixtureFile} not built."); return 0; }
    PluginDescriptor? desc = null;
    foreach (PluginDescriptor d in format.Scan([MacFixtures.Dir]))
        if (string.Equals(d.Name, pluginName, StringComparison.OrdinalIgnoreCase)) { desc = d; break; }
    if (desc == null) { Console.WriteLine($"FAIL {label}: fixture present but plugin '{pluginName}' not found in scan."); return 1; }
    return DoEmbed(label, format, desc, title);
}

// Find an installed plugin whose bundle name contains nameContains via the format's own discovery, then embed
// it. EnumerateFiles touches only the filesystem, so we filter to the target BEFORE loading any native code —
// only the matched bundle is scanned/loaded (no risk from other installed plugins). SKIPs when absent.
static int EmbedReal(string label, IPluginFormat format, string nameContains, string title)
{
    string? file = null;
    foreach (string f in format.EnumerateFiles(format.DefaultSearchPaths))
        if (Path.GetFileName(f).Contains(nameContains, StringComparison.OrdinalIgnoreCase)) { file = f; break; }
    if (file == null) { Console.WriteLine($"SKIP {label}: no installed {format.Name} plugin matching '{nameContains}'."); return 0; }

    PluginDescriptor? desc = null;
    foreach (PluginDescriptor d in format.ScanFile(file)) { desc = d; break; }
    if (desc == null) { Console.WriteLine($"SKIP {label}: '{Path.GetFileName(file)}' yielded no {format.Name} plugin."); return 0; }
    return DoEmbed(label, format, desc, title);
}

// AU v3 plugins are async-instantiated (effects out-of-process), whose completion is delivered on the MAIN
// dispatch queue — so AudioUnitFormat.Load's run-loop pump only sees it on the thread draining that queue, which
// this harness's Main (thread 0) provides and xUnit cannot. Loads the unit through the AUAudioUnit front-end
// (AudioUnitV3Plugin) and asserts it renders real audio (an effect alters a dry tone; an instrument sounds a
// note) plus a fullState round-trip. SKIPs when the unit isn't installed. Returns 1 on FAIL, 0 on PASS/SKIP.
static unsafe int RenderAuV3(string label, string idPrefix, bool isInstrument)
{
    var format = new AudioUnitFormat();
    PluginDescriptor? desc = null;
    foreach (PluginDescriptor d in format.Scan(format.DefaultSearchPaths))
        if (d.Id.StartsWith(idPrefix, StringComparison.Ordinal)) { desc = d; break; }
    if (desc == null) { Console.WriteLine($"SKIP {label}: no AU matching '{idPrefix}'."); return 0; }

    const int frames = 512;
    const double sr = 48000.0, amp = 0.5, freq = 1000.0;
    using IHostedPlugin plugin = format.Load(desc, new AudioConfig((int)sr, frames, ChannelCount: 2));   // async via the AUAudioUnit front-end

    var in0 = (float*)NativeMemory.AllocZeroed((nuint)frames, sizeof(float));
    var in1 = (float*)NativeMemory.AllocZeroed((nuint)frames, sizeof(float));
    var out0 = (float*)NativeMemory.AllocZeroed((nuint)frames, sizeof(float));
    var out1 = (float*)NativeMemory.AllocZeroed((nuint)frames, sizeof(float));
    var ins = (float**)NativeMemory.Alloc(2, (nuint)IntPtr.Size);
    var outs = (float**)NativeMemory.Alloc(2, (nuint)IntPtr.Size);
    ins[0] = in0; ins[1] = in1; outs[0] = out0; outs[1] = out1;
    try
    {
        var input = new PlanarBuffers(ins, 2, frames);
        var output = new PlanarBuffers(outs, 2, frames);

        double outRms = 0, diffRms = 0;
        long pos = 0;
        for (var blk = 0; blk < 16; blk++)
        {
            if (!isInstrument)
                for (var i = 0; i < frames; i++)
                {
                    var s = (float)(amp * Math.Sin(2.0 * Math.PI * freq * (pos + i) / sr));
                    in0[i] = s; in1[i] = s;
                }
            ReadOnlySpan<HostEvent> ev = isInstrument && blk == 0 ? [HostEvent.Midi(0, 0x90, 60, 100)] : default;
            plugin.Process(input, output, ev);
            pos += frames;
            if (blk == 15)
            {
                double so = 0, sd = 0;
                for (var i = 0; i < frames; i++)
                {
                    so += out0[i] * (double)out0[i];
                    double dry = isInstrument ? 0 : amp * Math.Sin(2.0 * Math.PI * freq * (pos - frames + i) / sr);
                    sd += (out0[i] - dry) * (out0[i] - dry);
                }
                outRms = Math.Sqrt(so / frames);
                diffRms = Math.Sqrt(sd / frames);
            }
        }

        bool ok = isInstrument
            ? outRms > 1e-3
            : outRms > 0.05 && diffRms > 0.01;   // effect must be non-silent AND differ from the dry signal
        if (!ok) { Console.WriteLine($"FAIL {label}: '{desc.Name}' silent/unprocessed (out RMS={outRms:F4}, diff={diffRms:F4})."); return 1; }

        // State over the bridge (kAudioUnitProperty_ClassInfo). Parameters are normalized [0..1]; find one that
        // actually responds, snapshot, move it, reload, and expect ClassInfo to restore it.
        string stateNote = "no responsive param";
        foreach (PluginParameter q in plugin.Parameters)
        {
            double saved = plugin.GetParameter(q.Index);
            double moved = saved < 0.5 ? 0.9 : 0.1;
            plugin.SetParameter(q.Index, moved);
            // "Responds" = the readback actually CHANGED (stepped params quantize, so don't require the exact target).
            if (Math.Abs(plugin.GetParameter(q.Index) - saved) < 0.05) { plugin.SetParameter(q.Index, saved); continue; }   // inert → next

            plugin.SetParameter(q.Index, saved);          // snapshot the unit with this param at `saved`
            byte[] blob = plugin.SaveState();
            if (blob.Length == 0) { stateNote = "no ClassInfo"; break; }
            plugin.SetParameter(q.Index, moved);          // move away, then reload
            plugin.LoadState(blob);
            double restored = plugin.GetParameter(q.Index);
            if (Math.Abs(restored - saved) > 0.05)
            { Console.WriteLine($"FAIL {label}: ClassInfo did not restore param '{q.Name}' (saved={saved:F3} → reloaded={restored:F3})."); return 1; }
            stateNote = $"state round-trips ('{q.Name}')";
            break;
        }
        Console.WriteLine($"PASS {label}: '{desc.Name}' via the AUAudioUnit front-end (out RMS={outRms:F4}, diff-from-dry={diffRms:F4}; {stateNote}).");
        return 0;
    }
    finally
    {
        NativeMemory.Free(in0); NativeMemory.Free(in1); NativeMemory.Free(out0); NativeMemory.Free(out1);
        NativeMemory.Free(ins); NativeMemory.Free(outs);
    }
}

// Load the plugin, open its editor on this (main) thread, and assert a child NSView embeds. Returns 1 on FAIL,
// 0 on PASS. Cocoa editor calls are main-thread-correct here.
static int DoEmbed(string label, IPluginFormat format, PluginDescriptor desc, string title)
{
    var config = new AudioConfig(48000, 512, ChannelCount: 2);
    using IHostedPlugin plugin = format.Load(desc, config);
    IPluginGui? gui = plugin.Gui;
    if (gui is not { HasEditor: true }) { Console.WriteLine($"FAIL {label}: '{desc.Name}' has no editor."); return 1; }
    if (!gui.IsApiSupported("cocoa", floating: false)) { Console.WriteLine($"FAIL {label}: IsApiSupported(\"cocoa\") was false."); return 1; }

    using EditorSession? session = EditorSession.Open(gui, title);
    if (session is not { IsOpen: true }) { Console.WriteLine($"FAIL {label}: session did not open ({session?.Error})."); return 1; }

    uint children = 0;
    for (var i = 0; i < 80 && children == 0; i++) { children = session.EmbeddedChildCount; if (children == 0) session.PumpOnce(25); }
    session.Close();
    if (children < 1) { Console.WriteLine($"FAIL {label}: no child NSView embedded."); return 1; }
    Console.WriteLine($"PASS {label}: '{desc.Name}' embedded {children} child NSView(s).");
    return 0;
}
