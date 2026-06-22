using System;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.MacEditorHarness;

// Runs on the process main thread (thread 0), which AppKit requires and xUnit cannot provide. Opens an
// EditorSession with a fake cocoa editor and confirms the host parented a child NSView, then closes. PASS/SKIP
// → exit 0 (CI-safe when headless); FAIL → exit 1.

if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP: Cocoa editor backend is macOS-only."); return 0; }
if (!EditorPlatform.Current.IsAvailable) { Console.WriteLine("SKIP: no window server (headless session)."); return 0; }

var gui = new FakeCocoaEditorGui(320, 240);
using EditorSession? session = EditorSession.Open(gui, "MidiSharp macOS editor harness");
if (session is not { IsOpen: true })
{
    Console.WriteLine($"FAIL: editor session did not open (error: {session?.Error}).");
    return 1;
}

uint children = 0;
for (var i = 0; i < 40 && children == 0; i++) { children = session.EmbeddedChildCount; if (children == 0) session.PumpOnce(25); }
if (children < 1)
{
    Console.WriteLine("FAIL: the fake editor did not embed a child NSView into the host window.");
    return 1;
}

session.PumpOnce(25);
session.Close();

Console.WriteLine($"PASS: embedded {children} child NSView(s) and closed cleanly.");
return 0;
