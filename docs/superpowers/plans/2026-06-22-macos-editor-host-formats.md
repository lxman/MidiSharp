# macOS Editor-Host Formats (Plan B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make CLAP, VST2, and VST3 plugin editors embed on macOS through the Cocoa backend (Plan A), by teaching the VST2/VST3 adapters the `cocoa`/`NSView` window API, and verify it against **three clean-room fixtures** (no third-party plugins are installed on this Mac) whose editors are real child `NSView`s.

**Architecture:** Plan A added the Cocoa `INativeEditorWindow` backend; the format adapters' `IPluginGui` paths only know `"x11"`/`"win32"`. CLAP already passes the window-API string straight through (expected no change). VST2 (`Vst2Plugin`) accepts `"cocoa"`; VST3 (`Vst3Plugin`) gains the `"NSView"` platform-type and accepts `"cocoa"`. `Vst3PlugFrame` needs **no** change — its `IRunLoop` hand-out is already gated to Linux/FreeBSD only, which correctly excludes macOS (a VST3 editor on macOS, like Windows, self-drives via the OS run loop). Verification runs on the **main thread** through the Plan A `MacEditorHarness` (xUnit can't host AppKit), loading each clean-room fixture and asserting a child `NSView` embeds.

**Why the harness, not xUnit, and why CLAP behaves here:** the Windows CLAP real-plugin test *hung in-process* because `EditorWindow` drives the editor on a **background thread** and CLAP deadlocks off the creation thread. The macOS harness opens the editor on the **process main thread** (which CLAP and AppKit both require), so the in-process embed is thread-correct — the macOS situation is actually friendlier for CLAP than the Windows in-process path was.

**Tech Stack:** C# (net10.0), Objective-C fixtures via clang (`-framework Cocoa`), `objc_msgSend`/AppKit, xUnit v3. **arm64 only.**

**Spec:** `docs/superpowers/specs/2026-06-22-macos-editor-host-design.md` (§5.2, §6, §7). Plan A (the Cocoa backend, the `MacEditorHarness`, and the `FakeCocoaEditorGui`) is a prerequisite.

**Fixture-packaging facts (grounded in the adapters):**
- `ClapFormat.EnumerateFiles` globs `*.clap` **files** + `NativeLibrary.TryLoad` → the CLAP fixture is a **flat dylib named `*.clap`** (not a bundle). Needs the `free-audio/clap` headers (`clap/clap.h`, `clap/ext/gui.h`) — **not present on this Mac**; the build script fetches them.
- `Vst2Format.Extensions` is `*.so` on non-Windows → the VST2 fixture is a **flat dylib named `*.so`**. Clean-room `AEffect`, no SDK headers.
- `Vst3Format` resolves the macOS bundle `Contents/MacOS/<name>` → the VST3 fixture is a real **`.vst3` bundle**. Clean-room C-API mirroring the in-repo `src/MidiSharp.Hosting.Vst3/Vst3Abi.cs` (no Steinberg headers needed; no existing VST3 fixture to copy).
- macOS Gatekeeper/library-validation: each built binary is **ad-hoc signed** (`codesign -s -`) so the host can `dlopen` it.

> **Implementation outcome (2026-06-22, `feature/macos-editor-host`):** The VST2 clean-room `.so` fixture
> shipped as planned (Task 1). For **VST3 and CLAP**, verification used a **real installed plugin (Surge XT)**
> rather than hand-rolled clean-room fixtures — the user's call once the VST3 C-API fixture's cost was concrete.
> The `MacEditorHarness` loads Surge's VST3 and CLAP on the main thread and confirms a child `NSView` embeds.
> VST3 got the `PlatformTypeNsView` + `"cocoa"` mapping (Task 2). **CLAP needed no adapter change** but did need
> a macOS **`.clap` bundle discovery** fix in `ClapFormat` (`EnumerateFileSystemEntries` + a `Contents/MacOS`
> resolver) plus adding the system `/Library/Audio/Plug-Ins/{VST3,CLAP}` dirs to the default search paths. Net:
> macOS matches the Windows pattern — real plugins for VST3/CLAP, fixture for VST2. The clean-room VST3/CLAP
> bundle fixtures described in Tasks 2–3 below were **not built**; Surge also surfaced two pre-existing,
> platform-agnostic host findings (CLAP `start/stop_processing` thread; `EditorSession` `gui.size` before
> `create`) — flagged, not fixed.

## Global Constraints

- **Build/test/run with `dotnet`** (the .NET 10 SDK is on `PATH`, `dotnet --version` → `10.0.301`). `timeout`/`gtimeout` is absent on this Mac.
- **Target framework:** `net10.0`. Build clean: **0 warnings, 0 errors**.
- **Dependency-free adapters:** the VST2/VST3 edits add no new package references. Fixtures are clean-room C/Objective-C built with clang, linking `-framework Cocoa`.
- **Do not regress the X11/Win32 paths.** Every adapter edit is purely additive (`"cocoa"` accepted alongside `"x11"`/`"win32"`).
- **Main thread for editor calls** (the harness `Main` is thread 0; the worker is too).
- **Fixture checks self-skip** when a fixture isn't built (the harness prints `SKIP …` and stays exit 0), never hard-fail — like `ClapLiveTests`.
- **VST3 platform-type strings:** `"X11EmbedWindowID"` (x11) / `"HWND"` (win32) / `"NSView"` (cocoa) — exact, from `Vst3Abi`.
- `nullable` is enabled in all touched projects.

---

## File Structure

- `src/MidiSharp.Hosting.Vst2/Vst2Plugin.cs` *(modify)* — accept `"cocoa"` in `IsApiSupported`/`SetParent`.
- `src/MidiSharp.Hosting.Vst3/Vst3Abi.cs` *(modify)* — add `PlatformTypeNsView = "NSView"`.
- `src/MidiSharp.Hosting.Vst3/Vst3Plugin.cs` *(modify)* — map `"cocoa"`→`"NSView"` in `PlatformTypeFor`.
- (CLAP `ClapPlugin.cs` and VST3 `Vst3PlugFrame.cs`: **no change** — confirmed, not edited.)
- `tests/fixtures/mac/vst2_gui_fixture.m` *(new)* — clean-room VST2 gain `.so` with an `NSView` editor.
- `tests/fixtures/mac/vst3_gui_fixture.m` *(new)* — clean-room VST3 `.vst3` bundle with an `NSView` editor.
- `tests/fixtures/mac/clap_gui_fixture.m` *(new)* — clean-room CLAP `.clap` with an `NSView` editor (needs clap headers).
- `tests/fixtures/mac/MidiSharpGui-vst3.plist` *(new)* — the VST3 bundle `Info.plist` template.
- `tests/fixtures/mac/build-fixtures.sh` *(new)* — clang build → `tests/fixtures/mac/out/`, lay out the VST3 bundle, ad-hoc sign.
- `tests/MidiSharp.Hosting.MacEditorHarness/MacFixtures.cs` *(new)* — resolves the fixture output dir.
- `tests/MidiSharp.Hosting.MacEditorHarness/Program.cs` *(modify)* — after the fake-gui check, embed each present fixture.
- `tests/MidiSharp.Hosting.MacEditorHarness/MacEditorHarness.csproj` *(modify)* — reference the format adapters.
- `.gitignore` *(modify)* — ignore `tests/fixtures/mac/out/`.

---

### Task 1: VST2 `cocoa` editor embedding + clean-room fixture + shared fixture infra

VST2 is the most self-contained fixture (clean-room `AEffect`, no SDK headers, a flat `.so`), so it establishes the shared infra: `build-fixtures.sh`, `MacFixtures.cs`, the harness fixture-embed helper, and the `.gitignore` entry. Then teach the adapter `"cocoa"` and verify the embed in the harness.

**Files:**
- Create: `tests/fixtures/mac/vst2_gui_fixture.m`
- Create: `tests/fixtures/mac/build-fixtures.sh`
- Create: `tests/MidiSharp.Hosting.MacEditorHarness/MacFixtures.cs`
- Modify: `tests/MidiSharp.Hosting.MacEditorHarness/MacEditorHarness.csproj`
- Modify: `tests/MidiSharp.Hosting.MacEditorHarness/Program.cs`
- Modify: `src/MidiSharp.Hosting.Vst2/Vst2Plugin.cs`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: `Vst2Format`, `IPluginGui`, `EditorSession` (Plan A), `INativeEditorWindow.EmbeddedChildCount`.
- Produces: `Vst2Plugin` accepts `"cocoa"`; `MacFixtures.Dir`; a harness `EmbedFixture(format, name, title)` helper reused by Tasks 2–3.

- [ ] **Step 1: Write the clean-room VST2 fixture (Objective-C, NSView editor)**

Create `tests/fixtures/mac/vst2_gui_fixture.m`:

```objc
/* Clean-room VST2 gain effect with a Cocoa (NSView) editor child. No Steinberg SDK headers — the AEffect ABI
 * is transcribed from MidiSharp's host-side Vst2Abi.cs. Name "MidiSharp VST2 Gain", uniqueID 'MsG2', one
 * "Gain" param (0..1 -> x0..x2), 300x200 editor. Built as a flat .so dylib (Vst2Format globs *.so on
 * non-Windows) by build-fixtures.sh (clang -framework Cocoa). */
#import <Cocoa/Cocoa.h>
#include <string.h>
#include <stdint.h>

typedef struct AEffect AEffect;
typedef intptr_t (*DispatcherFn)(AEffect*, int32_t, int32_t, intptr_t, void*, float);
typedef void (*SetParamFn)(AEffect*, int32_t, float);
typedef float (*GetParamFn)(AEffect*, int32_t);
typedef void (*ProcessReplacingFn)(AEffect*, float**, float**, int32_t);
typedef intptr_t (*AudioMasterFn)(AEffect*, int32_t, int32_t, intptr_t, void*, float);

struct AEffect {
    int32_t magic;
    DispatcherFn dispatcher;
    void* process;
    SetParamFn setParameter;
    GetParamFn getParameter;
    int32_t numPrograms, numParams, numInputs, numOutputs, flags;
    intptr_t resvd1, resvd2;
    int32_t initialDelay, realQualities, offQualities;
    float ioRatio;
    void* object; void* user;
    int32_t uniqueID, version;
    ProcessReplacingFn processReplacing;
    void* processDoubleReplacing;
    char future[56];
};

#define EFFECT_MAGIC 0x56737450 /* 'VstP' */
#define effClose 1
#define effGetParamName 8
#define effEditGetRect 13
#define effEditOpen 14
#define effEditClose 15
#define effEditIdle 19
#define effGetEffectName 45
#define effGetVendorString 47
#define effGetProductString 48
#define effGetVstVersion 58
#define effFlagsHasEditor 1
#define effFlagsCanReplacing 16

typedef struct { int16_t top, left, bottom, right; } ERect;

static float g_gain = 0.5f;
static ERect g_rect = { 0, 0, 200, 300 };   /* 300 wide x 200 tall */
static NSView* g_child = nil;
static AEffect g_effect;

static intptr_t dispatcher(AEffect* e, int32_t op, int32_t idx, intptr_t value, void* ptr, float opt) {
    (void)e; (void)idx; (void)value; (void)opt;
    switch (op) {
        case effGetEffectName:    strcpy((char*)ptr, "MidiSharp VST2 Gain"); return 1;
        case effGetVendorString:  strcpy((char*)ptr, "MidiSharp"); return 1;
        case effGetProductString: strcpy((char*)ptr, "MidiSharp VST2 Gain"); return 1;
        case effGetParamName:     strcpy((char*)ptr, "Gain"); return 1;
        case effGetVstVersion:    return 2400;
        case effEditGetRect:      *(ERect**)ptr = &g_rect; return 1;
        case effEditOpen: {
            /* ptr = parent NSView* on Cocoa. Add a real child NSView, exactly as a native editor would. */
            NSView* parent = (NSView*)ptr;
            g_child = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, 300, 200)];
            [parent addSubview:g_child];
            return g_child ? 1 : 0;
        }
        case effEditClose: if (g_child) { [g_child removeFromSuperview]; g_child = nil; } return 1;
        case effEditIdle:  return 1;
        case effClose:     return 1;
        default:           return 0;
    }
}
static void setParameter(AEffect* e, int32_t idx, float val) { (void)e; if (idx == 0) g_gain = val; }
static float getParameter(AEffect* e, int32_t idx) { (void)e; return idx == 0 ? g_gain : 0.0f; }
static void processReplacing(AEffect* e, float** in, float** out, int32_t frames) {
    (void)e;
    float g = g_gain * 2.0f;
    for (int c = 0; c < 2; c++)
        for (int32_t i = 0; i < frames; i++)
            out[c][i] = in[c][i] * g;
}

__attribute__((visibility("default"))) AEffect* VSTPluginMain(AudioMasterFn host) {
    (void)host;
    memset(&g_effect, 0, sizeof(g_effect));
    g_effect.magic = EFFECT_MAGIC;
    g_effect.dispatcher = dispatcher;
    g_effect.setParameter = setParameter;
    g_effect.getParameter = getParameter;
    g_effect.numParams = 1;
    g_effect.numInputs = 2;
    g_effect.numOutputs = 2;
    g_effect.flags = effFlagsHasEditor | effFlagsCanReplacing;
    g_effect.uniqueID = 0x4D734732; /* 'MsG2' */
    g_effect.version = 1;
    g_effect.processReplacing = processReplacing;
    return &g_effect;
}
```

> If `Vst2Format`'s export lookup (`Vst2Format.cs:118`) expects `main` as well as `VSTPluginMain`, add a `main` alias: `__attribute__((visibility("default"))) AEffect* main(AudioMasterFn h) { return VSTPluginMain(h); }`. Confirm against `Vst2Format.cs` while implementing.

- [ ] **Step 2: Write the build script (shared infra)**

Create `tests/fixtures/mac/build-fixtures.sh` (executable, `chmod +x`). It starts with just the VST2 line; Tasks 2–3 append the VST3 and CLAP lines:

```bash
#!/usr/bin/env bash
# Builds the macOS clean-room native plugin fixtures into ./out (git-ignored). arm64, ad-hoc signed.
set -euo pipefail
root="$(cd "$(dirname "$0")" && pwd)"
out="$root/out"
mkdir -p "$out"
clang="${CLANG:-/usr/bin/clang}"

# ── VST2: flat dylib named .so (Vst2Format globs *.so on non-Windows) ──
"$clang" -x objective-c -shared -O2 -fno-objc-arc -framework Cocoa \
  -o "$out/midisharp_gui_vst2.so" "$root/vst2_gui_fixture.m"
codesign -s - "$out/midisharp_gui_vst2.so"
echo "Built: $out/midisharp_gui_vst2.so"
```

- [ ] **Step 3: Ignore the fixture output and build the VST2 fixture**

Append to `.gitignore`:

```
tests/fixtures/mac/out/
```

Run: `chmod +x tests/fixtures/mac/build-fixtures.sh && tests/fixtures/mac/build-fixtures.sh`
Expected: prints `Built: …/out/midisharp_gui_vst2.so`. Confirm the file exists and `codesign -dv` reports an ad-hoc signature.

- [ ] **Step 4: Add the fixture-path helper to the harness**

Create `tests/MidiSharp.Hosting.MacEditorHarness/MacFixtures.cs`:

```csharp
using System;
using System.IO;

namespace MidiSharp.Hosting.MacEditorHarness;

/// <summary>Locates the built macOS native test fixtures (see tests/fixtures/mac/build-fixtures.sh). Override
/// with the MIDISHARP_MAC_FIXTURES env var; otherwise resolved relative to the harness assembly.</summary>
internal static class MacFixtures
{
    public static string Dir
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("MIDISHARP_MAC_FIXTURES");
            if (!string.IsNullOrEmpty(env)) return env;
            // bin/Debug/net10.0 -> repo root is five levels up, then tests/fixtures/mac/out.
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "tests", "fixtures", "mac", "out"));
        }
    }

    public static bool Has(string fileName) => File.Exists(Path.Combine(Dir, fileName))
                                            || Directory.Exists(Path.Combine(Dir, fileName));
}
```

- [ ] **Step 5: Reference the format adapters from the harness**

Edit `tests/MidiSharp.Hosting.MacEditorHarness/MacEditorHarness.csproj` — add to the existing `<ItemGroup>` of `ProjectReference`s:

```xml
    <ProjectReference Include="..\..\src\MidiSharp.Hosting.Clap\MidiSharp.Hosting.Clap.csproj" />
    <ProjectReference Include="..\..\src\MidiSharp.Hosting.Vst2\MidiSharp.Hosting.Vst2.csproj" />
    <ProjectReference Include="..\..\src\MidiSharp.Hosting.Vst3\MidiSharp.Hosting.Vst3.csproj" />
```

- [ ] **Step 6: Add the fixture-embed helper + VST2 check to the harness**

Edit `tests/MidiSharp.Hosting.MacEditorHarness/Program.cs`. After the fake-gui block (before the final `return 0;`), add the VST2 fixture check, and add the shared helper at the bottom of the file. The helper loads a fixture through its format adapter and embeds it on the main thread, returning true on PASS, false on FAIL, null on SKIP (fixture absent):

```csharp
// ── Per-format fixture embeds (each SKIPs when its fixture isn't built) ──
var failures = 0;

failures += Embed("VST2", new MidiSharp.Hosting.Vst2.Vst2Format(), "midisharp_gui_vst2.so",
    "MidiSharp VST2 Gain", "VST2 cocoa editor");

return failures == 0 ? 0 : 1;

// Loads the named fixture via its format adapter, opens its editor on this (main) thread, and asserts a child
// NSView embeds. Returns 1 on FAIL, 0 on PASS/SKIP. Cocoa editor calls are main-thread-correct here.
static int Embed(string label, MidiSharp.Hosting.IPluginFormat format, string fixtureFile, string pluginName, string title)
{
    if (!MidiSharp.Hosting.MacEditorHarness.MacFixtures.Has(fixtureFile)) { Console.WriteLine($"SKIP {label}: fixture {fixtureFile} not built."); return 0; }

    var config = new MidiSharp.Hosting.AudioConfig(48000, 512, ChannelCount: 2);
    MidiSharp.Hosting.PluginDescriptor? desc = null;
    foreach (var d in format.Scan([MidiSharp.Hosting.MacEditorHarness.MacFixtures.Dir]))
        if (d.Name == pluginName || string.Equals(d.Name, pluginName, StringComparison.OrdinalIgnoreCase)) { desc = d; break }
    if (desc == null) { Console.WriteLine($"FAIL {label}: fixture loaded but plugin '{pluginName}' not found in scan."); return 1; }

    using MidiSharp.Hosting.IHostedPlugin plugin = format.Load(desc, config);
    if (plugin.Gui is not { HasEditor: true } gui) { Console.WriteLine($"FAIL {label}: no editor on the fixture."); return 1; }
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
```

> Fix the obvious typo when transcribing (`break;`). Top-level statements require the local function `Embed` to be declared after the executable statements — keep it at the end of the file. `EditorSession` is in `MidiSharp.Hosting.EditorHost` (already `using`-imported by Plan A's `Program.cs`).

- [ ] **Step 7: Run the harness to confirm the VST2 embed FAILS (adapter not yet updated)**

Run: `dotnet run --project tests/MidiSharp.Hosting.MacEditorHarness -c Debug`
Expected: `PASS` for the fake gui, then `FAIL VST2: IsApiSupported("cocoa") was false.` exit 1 — the adapter still gates on `"x11"`/`"win32"`.

- [ ] **Step 8: Accept `cocoa` in the VST2 adapter**

In `src/MidiSharp.Hosting.Vst2/Vst2Plugin.cs`, replace:

```csharp
    bool IPluginGui.IsApiSupported(string windowApi, bool floating)
        => (_eff->Flags & FlagsHasEditor) != 0 && windowApi is "x11" or "win32";   // X11 on Linux, HWND on Windows
```

with:

```csharp
    bool IPluginGui.IsApiSupported(string windowApi, bool floating)
        => (_eff->Flags & FlagsHasEditor) != 0 && windowApi is "x11" or "win32" or "cocoa";   // X11 / HWND / NSView
```

and replace:

```csharp
    bool IPluginGui.SetParent(string windowApi, ulong windowHandle)
    {
        if ((_eff->Flags & FlagsHasEditor) == 0 || windowApi is not ("x11" or "win32")) return false;
        _eff->Dispatcher(_eff, EffEditOpen, 0, IntPtr.Zero, (void*)(nuint)windowHandle, 0);   // ptr = parent HWND on Windows
        _editorOpen = true;
        return true;
    }
```

with:

```csharp
    bool IPluginGui.SetParent(string windowApi, ulong windowHandle)
    {
        if ((_eff->Flags & FlagsHasEditor) == 0 || windowApi is not ("x11" or "win32" or "cocoa")) return false;
        _eff->Dispatcher(_eff, EffEditOpen, 0, IntPtr.Zero, (void*)(nuint)windowHandle, 0);   // ptr = parent NSView* on macOS
        _editorOpen = true;
        return true;
    }
```

(The exact surrounding lines may differ from Win32 Plan B's — match the current file; the change is purely the additive `or "cocoa"`.)

- [ ] **Step 9: Run the harness to confirm the VST2 embed PASSES**

Run: `dotnet run --project tests/MidiSharp.Hosting.MacEditorHarness -c Debug`
Expected: `PASS VST2: embedded 1 child NSView(s).` exit 0 — the fixture's `effEditOpen` adds a child NSView under the host content view.

- [ ] **Step 10: Build clean and commit**

Run: `dotnet build MidiSharp.slnx -c Debug` → 0 warnings, 0 errors.

```bash
git add src/MidiSharp.Hosting.Vst2/Vst2Plugin.cs tests/fixtures/mac/vst2_gui_fixture.m tests/fixtures/mac/build-fixtures.sh tests/MidiSharp.Hosting.MacEditorHarness/MacFixtures.cs tests/MidiSharp.Hosting.MacEditorHarness/MacEditorHarness.csproj tests/MidiSharp.Hosting.MacEditorHarness/Program.cs .gitignore
git commit -m "VST2: embed editors via NSView on macOS + clean-room cocoa fixture"
```

---

### Task 2: VST3 `cocoa`/`NSView` editor embedding + clean-room bundle fixture

Teach the VST3 adapter the `"cocoa"`→`"NSView"` mapping, then verify with a clean-room `.vst3` **bundle** whose `IPlugView` attaches a child NSView. The fixture's C-API mirrors the in-repo `Vst3Abi.cs` transcription (no Steinberg headers).

**Files:**
- Modify: `src/MidiSharp.Hosting.Vst3/Vst3Abi.cs`
- Modify: `src/MidiSharp.Hosting.Vst3/Vst3Plugin.cs`
- Create: `tests/fixtures/mac/vst3_gui_fixture.m`
- Create: `tests/fixtures/mac/MidiSharpGui-vst3.plist`
- Modify: `tests/fixtures/mac/build-fixtures.sh` (append the VST3 build + bundle layout)
- Modify: `tests/MidiSharp.Hosting.MacEditorHarness/Program.cs` (append the VST3 embed check)

**Interfaces:**
- Consumes: `Vst3Format`, `Vst3Abi`, `IPluginGui`, the harness `Embed` helper (Task 1).
- Produces: `Vst3Plugin` returns true from `IsApiSupported("cocoa", false)`/`SetParent("cocoa", nsview)` for an editor-capable VST3.

- [ ] **Step 1: Add the `NSView` platform-type constant**

In `src/MidiSharp.Hosting.Vst3/Vst3Abi.cs`, add directly after `PlatformTypeHwnd` (line 30):

```csharp
    public const string PlatformTypeNsView = "NSView";
```

- [ ] **Step 2: Map `cocoa`→`NSView` in `Vst3Plugin.PlatformTypeFor`**

In `src/MidiSharp.Hosting.Vst3/Vst3Plugin.cs`, replace `PlatformTypeFor`:

```csharp
    private static string? PlatformTypeFor(string windowApi) => windowApi switch
    {
        "x11" => PlatformTypeX11,
        "win32" => PlatformTypeHwnd,
        _ => null,
    };
```

with:

```csharp
    private static string? PlatformTypeFor(string windowApi) => windowApi switch
    {
        "x11" => PlatformTypeX11,
        "win32" => PlatformTypeHwnd,
        "cocoa" => PlatformTypeNsView,
        _ => null,
    };
```

`IsApiSupported`/`SetParent` already use `PlatformTypeFor` and a `stackalloc byte[20]` — `"NSView"` is 6 + NUL, well within 20. **No `Vst3PlugFrame` change:** its run-loop hand-out is gated to `OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()` (`Vst3PlugFrame.cs:68`), which already excludes macOS. Confirm by reading that line — do not edit it.

- [ ] **Step 3: Write the clean-room VST3 bundle fixture**

The fixture is a minimal single-component VST3 exposing an `IEditController` whose `createView` returns an `IPlugView` that supports `"NSView"` and, on `attached(view, "NSView")`, adds a child NSView. **Transcribe the C-API vtable layouts and IIDs from `src/MidiSharp.Hosting.Vst3/Vst3Abi.cs`** so host and fixture agree exactly (clean-room; no Steinberg SDK). The macOS-specific editor body is:

```objc
/* In the IPlugView implementation (see Vst3Abi.cs for the IPlugView/IPlugViewVtbl, IEditController,
 * IPluginFactory, and IID layouts to mirror). Only the platform branch is shown — the rest is ABI plumbing. */
#import <Cocoa/Cocoa.h>

static NSView* g_child = nil;

/* tresult isPlatformTypeSupported(void* self, const char* type) — kResultTrue(0) for "NSView". */
static int32_t view_isPlatformTypeSupported(void* self, const char* type) {
    (void)self;
    return (type && strcmp(type, "NSView") == 0) ? 0 /*kResultTrue*/ : 1 /*kResultFalse*/;
}

/* tresult attached(void* self, void* parent, const char* type) — parent is an NSView* for "NSView". */
static int32_t view_attached(void* self, void* parent, const char* type) {
    (void)self;
    if (!type || strcmp(type, "NSView") != 0 || !parent) return 1 /*kResultFalse*/;
    g_child = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, 320, 240)];
    [(NSView*)parent addSubview:g_child];
    return 0; /*kResultTrue*/
}

/* tresult getSize(void* self, ViewRect* r) — 320x240; removed(self) -> [g_child removeFromSuperview]. */
```

The build script (Step 5) compiles this `.m` to a Mach-O at `MidiSharpGui.vst3/Contents/MacOS/MidiSharpGui`. Export the factory entry the loader expects — confirm the export name in `Vst3Format.cs`/`Vst3Abi.cs` (`GetPluginFactory`) while implementing, and mirror its `IPluginFactory`/`PFactoryInfo`/`PClassInfo` shape.

> This is the heaviest fixture (no existing VST3 fixture, no Steinberg headers). Build it incrementally against `Vst3Abi.cs`: get `GetPluginFactory` enumerating one class first (scan finds it), then `IComponent`/`IEditController` load, then `createView`→`IPlugView`→`attached`. The harness embed is the gate. If a step stalls, report `DONE_WITH_CONCERNS` with the failing sub-step rather than guessing at VST3 semantics.

- [ ] **Step 4: Write the VST3 bundle Info.plist**

Create `tests/fixtures/mac/MidiSharpGui-vst3.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>MidiSharpGui</string>
    <key>CFBundleIdentifier</key><string>com.midisharp.test.gui.vst3</string>
    <key>CFBundleName</key><string>MidiSharpGui</string>
    <key>CFBundlePackageType</key><string>BNDL</string>
    <key>CFBundleVersion</key><string>1.0.0</string>
    <key>CFBundleShortVersionString</key><string>1.0.0</string>
</dict>
</plist>
```

- [ ] **Step 5: Append the VST3 build + bundle layout to `build-fixtures.sh`**

Add to `tests/fixtures/mac/build-fixtures.sh` (after the VST2 block):

```bash
# ── VST3: a .vst3 bundle (Vst3Format resolves Contents/MacOS/<name> on macOS) ──
vst3="$out/MidiSharpGui.vst3"
mkdir -p "$vst3/Contents/MacOS"
cp "$root/MidiSharpGui-vst3.plist" "$vst3/Contents/Info.plist"
"$clang" -x objective-c -shared -O2 -fno-objc-arc -framework Cocoa \
  -o "$vst3/Contents/MacOS/MidiSharpGui" "$root/vst3_gui_fixture.m"
codesign -s - "$vst3"
echo "Built: $vst3"
```

Run: `tests/fixtures/mac/build-fixtures.sh` → expect `Built: …/out/MidiSharpGui.vst3`.

- [ ] **Step 6: Append the VST3 embed check to the harness**

In `tests/MidiSharp.Hosting.MacEditorHarness/Program.cs`, add another `Embed(...)` call next to the VST2 one (before `return`):

```csharp
failures += Embed("VST3", new MidiSharp.Hosting.Vst3.Vst3Format(), "MidiSharpGui.vst3",
    "MidiSharpGui", "VST3 cocoa editor");
```

(Match `pluginName` to the descriptor name your fixture's factory reports.)

- [ ] **Step 7: Run the harness; iterate the fixture to PASS**

Run: `dotnet run --project tests/MidiSharp.Hosting.MacEditorHarness -c Debug`
Expected once the adapter change + fixture are in place: `PASS VST3: embedded 1 child NSView(s).` Build the fixture up against this gate (Step 3 note). The adapter change alone is verified the moment `IsApiSupported("cocoa")` returns true.

- [ ] **Step 8: Build clean and commit**

Run: `dotnet build MidiSharp.slnx -c Debug` → 0/0.

```bash
git add src/MidiSharp.Hosting.Vst3/Vst3Abi.cs src/MidiSharp.Hosting.Vst3/Vst3Plugin.cs tests/fixtures/mac/vst3_gui_fixture.m tests/fixtures/mac/MidiSharpGui-vst3.plist tests/fixtures/mac/build-fixtures.sh tests/MidiSharp.Hosting.MacEditorHarness/Program.cs
git commit -m "VST3: embed editors via NSView on macOS + clean-room cocoa bundle fixture"
```

---

### Task 3: CLAP `cocoa` editor verification (no adapter change) + clean-room fixture

CLAP passes the window API straight through (`ClapPlugin.SetParent` builds a `clap_window { api, handle }` and calls the plugin), and `WindowApiCocoa = "cocoa"` already exists — so the adapter should need **no change**. Prove it with a clean-room CLAP fixture (a flat `.clap` dylib) whose `clap.gui` `set_parent` adds a child NSView.

**Files:**
- Create: `tests/fixtures/mac/clap_gui_fixture.m`
- Modify: `tests/fixtures/mac/build-fixtures.sh` (append the CLAP build; fetch clap headers)
- Modify: `tests/MidiSharp.Hosting.MacEditorHarness/Program.cs` (append the CLAP embed check)
- (Only if the harness fails) Modify: `src/MidiSharp.Hosting.Clap/ClapPlugin.cs`.

**Interfaces:**
- Consumes: `ClapFormat`, `IPluginGui`, the harness `Embed` helper.
- Produces: confirmation that CLAP cocoa editor embedding works unchanged (or the minimal fix if not).

- [ ] **Step 1: Write the clean-room CLAP fixture (mirrors the existing win fixture, cocoa editor)**

Create `tests/fixtures/mac/clap_gui_fixture.m`, modeled on `tests/fixtures/win/clap_gui_fixture.c` (same `clap_plugin_descriptor` id `midisharp.test.gui`, `clap.gui` extension, `clap.timer-support`), replacing the `<windows.h>`/`WS_CHILD` editor with Cocoa. The `gui.set_parent` body:

```objc
#import <Cocoa/Cocoa.h>
#include <clap/clap.h>
#include <clap/ext/gui.h>

static NSView* g_child = nil;

/* bool set_parent(const clap_plugin_t* plugin, const clap_window_t* window) — window->cocoa is the parent
 * NSView* when window->api == CLAP_WINDOW_API_COCOA. Add a child NSView and return immediately (no blocking;
 * the host pumps the run loop). */
static bool gui_set_parent(const clap_plugin_t* plugin, const clap_window_t* window) {
    (void)plugin;
    if (!window || strcmp(window->api, CLAP_WINDOW_API_COCOA) != 0) return false;
    NSView* parent = (NSView*)window->cocoa;
    g_child = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, 320, 240)];
    [parent addSubview:g_child];
    return true;
}

/* gui.is_api_supported -> (strcmp(api, CLAP_WINDOW_API_COCOA) == 0 && !is_floating);
 * gui.get_size -> 320x240; gui.create/destroy/show/hide as in the win fixture (no-ops besides create/destroy). */
```

The rest of the file (entry point `clap_entry`, factory, descriptor, audio-effect plumbing, the `clap.timer-support` tick counter) is the same as the win fixture — copy it and adjust the includes/editor. Keep the second no-editor id `midisharp.test.gain` to mirror the existing pair.

- [ ] **Step 2: Append the CLAP build to `build-fixtures.sh` (fetch headers if absent)**

Add to `tests/fixtures/mac/build-fixtures.sh` (after the VST3 block). CLAP headers are header-only (MIT); fetch them into a cache if not provided via `$CLAP_INCLUDE`:

```bash
# ── CLAP: flat dylib named .clap (ClapFormat globs *.clap files + NativeLibrary.TryLoad). Needs clap headers. ──
clap_inc="${CLAP_INCLUDE:-$root/.clap-headers/include}"
if [ ! -f "$clap_inc/clap/clap.h" ]; then
  echo "Fetching clap headers (free-audio/clap, MIT)…"
  git clone --depth 1 https://github.com/free-audio/clap "$root/.clap-headers"
  clap_inc="$root/.clap-headers/include"
fi
"$clang" -x objective-c -shared -O2 -fno-objc-arc -framework Cocoa -I "$clap_inc" \
  -o "$out/midisharp_gui.clap" "$root/clap_gui_fixture.m"
codesign -s - "$out/midisharp_gui.clap"
echo "Built: $out/midisharp_gui.clap"
```

Also add `tests/fixtures/mac/.clap-headers/` to `.gitignore` (alongside the `out/` entry) so the fetched headers aren't committed.

Run: `tests/fixtures/mac/build-fixtures.sh` → expect `Built: …/out/midisharp_gui.clap` (after a one-time header clone; network required once).

- [ ] **Step 3: Append the CLAP embed check to the harness**

In `tests/MidiSharp.Hosting.MacEditorHarness/Program.cs`, add:

```csharp
failures += Embed("CLAP", new MidiSharp.Hosting.Clap.ClapFormat(), "midisharp_gui.clap",
    "MidiSharp Test GUI", "CLAP cocoa editor");
```

(Match `pluginName` to the fixture descriptor's `name`.)

- [ ] **Step 4: Run the harness to verify CLAP embeds unchanged**

Run: `dotnet run --project tests/MidiSharp.Hosting.MacEditorHarness -c Debug`
Expected: **PASS CLAP** with no adapter change — confirming CLAP's pass-through works on cocoa. The macOS harness runs on the main thread, so CLAP's GUI calls are thread-correct (unlike the Windows in-process background-thread path that hung). If it FAILS, the most likely cause is `ClapPlugin`/`ClapHost` editor-context wiring; make the **minimal** change, re-run, and document exactly what changed and why.

- [ ] **Step 5: Build clean, run the full suite, and commit**

Run: `dotnet build MidiSharp.slnx -c Debug` → 0/0.
Run: `dotnet test tests/MidiSharp.Hosting.Tests` → all pass; X11/Win32 and not-built-fixture tests SKIP; 0 failed.
Run: `dotnet run --project tests/MidiSharp.Hosting.MacEditorHarness -c Debug` → fake + VST2 + VST3 + CLAP all PASS, exit 0.

```bash
git add tests/fixtures/mac/clap_gui_fixture.m tests/fixtures/mac/build-fixtures.sh tests/MidiSharp.Hosting.MacEditorHarness/Program.cs .gitignore
# include ClapPlugin.cs ONLY if a fix was required
git commit -m "CLAP: verify cocoa editor embedding against a clean-room fixture"
```

---

## Self-Review

**Spec coverage (§5.2, §6, §7):**
- §5.2 VST2 accept cocoa + effEditOpen NSView → Task 1. ✓
- §5.2 VST3 add `NSView`, map cocoa, no `Vst3PlugFrame` change → Task 2. ✓ (the run-loop gate already excludes macOS.)
- §5.2 CLAP no change → Task 3 (verified against a clean-room fixture). ✓
- §6 fixtures: three clean-room fixtures (VST2 `.so`, VST3 `.vst3` bundle, CLAP `.clap`) + `build-fixtures.sh` (ad-hoc signed) + `out/` ignore; CLAP headers fetched (MIT). ✓
- §7 tests: per-format embed on the **main thread** via the harness (xUnit can't host AppKit), self-skipping when a fixture is absent. ✓

**Placeholder scan:** The VST2 fixture is inlined complete. The CLAP and VST3 fixtures are specified at implementable fidelity — the macOS-specific editor branch is given in full; the format ABI plumbing is delegated to named in-repo references (`tests/fixtures/win/clap_gui_fixture.c` for CLAP, `src/MidiSharp.Hosting.Vst3/Vst3Abi.cs` for VST3) rather than re-transcribed, matching how the Windows Plan B handled its CLAP fixture (briefed, not inlined). Each fixture has a measured harness gate and an explicit `DONE_WITH_CONCERNS` escape hatch. No TBD/TODO. ✓

**Type consistency:** `PlatformTypeNsView` defined in Task 2 (Vst3Abi), used in `PlatformTypeFor` (Vst3Plugin) same task. The harness `Embed(string, IPluginFormat, string, string, string)` helper is defined in Task 1 and reused verbatim in Tasks 2–3. Fixture names match: `"MidiSharp VST2 Gain"`/`midisharp_gui_vst2.so` (Task 1), `"MidiSharpGui"`/`MidiSharpGui.vst3` (Task 2), `"MidiSharp Test GUI"`/`midisharp_gui.clap` (Task 3) — each `Embed(...)` call's `pluginName`/`fixtureFile` are flagged to match the descriptor the fixture reports. All adapter edits are additive (`or "cocoa"` / a new switch arm), keeping `"x11"`/`"win32"` unchanged. ✓

**Discovery grounding:** CLAP fixture is a flat `*.clap` file (ClapFormat globs files); VST2 fixture is a flat `*.so` (Vst2Format globs `*.so` on non-Windows); VST3 fixture is a `.vst3` bundle (Vst3Format resolves `Contents/MacOS/<name>`). These match the adapters as they exist today — **no discovery change is needed for the fixtures**. (Real-bundle CLAP and `.vst`-bundle VST2 *discovery* on macOS are pre-existing gaps unrelated to editor embedding; out of scope.)

**Risk note for the executor:** the VST3 fixture is the heaviest (clean-room C-API, no existing fixture, no Steinberg headers) — build it incrementally against `Vst3Abi.cs` with the harness as the gate. The CLAP fixture needs a one-time `git clone` of the MIT clap headers (network). If a real CLAP/VST3 plugin with an editor gets installed later, the same `Embed` helper verifies it by pointing `MIDISHARP_MAC_FIXTURES` at its directory — stronger proof, no new code.
