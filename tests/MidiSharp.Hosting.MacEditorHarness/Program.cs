using System;
using System.IO;
using MidiSharp.Hosting;
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

return failures == 0 ? 0 : 1;

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
