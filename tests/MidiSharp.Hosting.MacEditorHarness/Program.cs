using System;
using MidiSharp.Hosting;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.MacEditorHarness;

// Runs on the process main thread (thread 0), which AppKit requires and xUnit cannot provide. Verifies the
// Cocoa editor backend: first a managed fake editor (always), then each clean-room format fixture that is built.
// Each check prints PASS/FAIL/SKIP; exit 0 unless something present FAILED.

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

// ── Per-format fixture embeds (each SKIPs when its fixture isn't built) ──
failures += Embed("VST2", new MidiSharp.Hosting.Vst2.Vst2Format(), "midisharp_gui_vst2.so",
    "MidiSharp VST2 Gain", "VST2 cocoa editor");

return failures == 0 ? 0 : 1;

// Loads the named fixture via its format adapter, opens its editor on this (main) thread, and asserts a child
// NSView embeds. Returns 1 on FAIL, 0 on PASS/SKIP. Cocoa editor calls are main-thread-correct here.
static int Embed(string label, IPluginFormat format, string fixtureFile, string pluginName, string title)
{
    if (!MacFixtures.Has(fixtureFile)) { Console.WriteLine($"SKIP {label}: fixture {fixtureFile} not built."); return 0; }

    var config = new AudioConfig(48000, 512, ChannelCount: 2);
    PluginDescriptor? desc = null;
    foreach (PluginDescriptor d in format.Scan([MacFixtures.Dir]))
        if (string.Equals(d.Name, pluginName, StringComparison.OrdinalIgnoreCase)) { desc = d; break; }
    if (desc == null) { Console.WriteLine($"FAIL {label}: fixture present but plugin '{pluginName}' not found in scan."); return 1; }

    using IHostedPlugin plugin = format.Load(desc, config);
    IPluginGui? gui = plugin.Gui;
    if (gui is not { HasEditor: true }) { Console.WriteLine($"FAIL {label}: no editor on the fixture."); return 1; }
    if (!gui.IsApiSupported("cocoa", floating: false)) { Console.WriteLine($"FAIL {label}: IsApiSupported(\"cocoa\") was false."); return 1; }

    using EditorSession? session = EditorSession.Open(gui, title);
    if (session is not { IsOpen: true }) { Console.WriteLine($"FAIL {label}: session did not open ({session?.Error})."); return 1; }

    uint children = 0;
    for (var i = 0; i < 40 && children == 0; i++) { children = session.EmbeddedChildCount; if (children == 0) session.PumpOnce(25); }
    session.Close();
    if (children < 1) { Console.WriteLine($"FAIL {label}: no child NSView embedded."); return 1; }
    Console.WriteLine($"PASS {label}: embedded {children} child NSView(s).");
    return 0;
}
