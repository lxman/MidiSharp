# Windows Editor-Host Formats (Plan B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make CLAP, VST2, and VST3 plugin editors embed on Windows through the Win32 backend (Plan A), by teaching the VST2/VST3 adapters the `win32` window API, and verify it against **real plugins** (CLAP `ChowMultiTool`, VST3 u-he `Podolski`/`Protoverb`) plus a clean-room **VST2** fixture (no real VST2 is installed).

**Architecture:** Plan A added the Win32 `INativeEditorWindow` backend; the format adapters' `IPluginGui` paths are still X11-hardcoded. CLAP already passes the window-API string straight through (expected no change). VST2 (`Vst2Plugin`) and VST3 (`Vst3Plugin`) gate on `"x11"` and need to accept `"win32"`; VST3 also needs the `"HWND"` platform-type string and must not offer the Linux `IRunLoop` on Windows (Windows VST3 editors self-drive via the message pump). Verification is real-plugin-first: live tests that self-skip when the plugin/desktop is absent, consistent with the project's existing `ClapLiveTests`/`Vst2Tests` pattern.

**Tech Stack:** C# (net10.0), `user32.dll` P/Invoke, clang (VST2 fixture), xUnit v3.

**Spec:** `docs/superpowers/specs/2026-06-21-windows-editor-host-design.md` (§5.2, §6, §7). Plan A (the Win32 backend) is already merged to `master`.

**Verification assets present on this machine** (confirmed): `C:\Program Files\Common Files\CLAP\ChowMultiTool.clap`; `C:\Program Files\Common Files\VST3\Podolski(x64).vst3` and `Protoverb(x64).vst3` (bare-DLL `.vst3`, both with editors). clang at `C:\clang\bin\clang`.

## Global Constraints

- **Target framework:** `net10.0`. Build clean: **0 warnings, 0 errors**.
- **Dependency-free:** adapters add no new package references. The VST2 fixture is clean-room C (no Steinberg SDK headers), built with clang; it may link `user32`.
- **Do not regress the X11 path.** Every adapter edit must keep `"x11"` working exactly as before; the change is purely additive (`"win32"` accepted alongside `"x11"`).
- **One UI thread** for editor calls (already enforced by `EditorSession`/`EditorWindow`).
- **Real-plugin tests self-skip** when off Windows, on a non-interactive desktop, or when the target plugin is absent — never hard-fail (they are inert in CI, like `ClapLiveTests`). Use `Assert.SkipWhen(...)`.
- **VST3 platform-type strings:** `"X11EmbedWindowID"` (x11) / `"HWND"` (win32) — exact, from `Vst3Abi`.
- `nullable` is enabled in all touched projects.

---

## File Structure

- `src/MidiSharp.Hosting.Vst3/Vst3Abi.cs` *(modify)* — add `PlatformTypeHwnd = "HWND"`.
- `src/MidiSharp.Hosting.Vst3/Vst3Plugin.cs` *(modify)* — map `windowApi`→platform-type in `IsApiSupported`/`SetParent`.
- `src/MidiSharp.Hosting.Vst3/Vst3PlugFrame.cs` *(modify)* — gate `IidRunLoop` hand-out to Linux only.
- `src/MidiSharp.Hosting.Vst2/Vst2Plugin.cs` *(modify)* — accept `"win32"` in `IsApiSupported`/`SetParent`.
- `tests/fixtures/win/vst2_gain_fixture.c` *(new)* — clean-room VST2 gain `.dll` with a `WS_CHILD` HWND editor.
- `tests/fixtures/win/build-fixtures.ps1` *(new)* — clang build → `tests/fixtures/win/out/`.
- `tests/MidiSharp.Hosting.Tests/WinFixtures.cs` *(new)* — resolves the fixture output dir for tests.
- `tests/MidiSharp.Hosting.Tests/Vst3WindowsEditorTests.cs` *(new)* — real VST3 editor embed (Podolski/Protoverb).
- `tests/MidiSharp.Hosting.Tests/ClapWindowsEditorTests.cs` *(new)* — real CLAP editor embed (ChowMultiTool).
- `tests/MidiSharp.Hosting.Tests/Vst2WindowsEditorTests.cs` *(new)* — VST2 fixture editor embed.
- `.gitignore` *(modify)* — ignore `tests/fixtures/win/out/`.

---

### Task 1: VST3 `win32` editor embedding + real-plugin test

Teach the VST3 adapter the `win32`/`HWND` editor path and gate the Linux run loop off on Windows; verify a real u-he plugin's editor embeds.

**Files:**
- Modify: `src/MidiSharp.Hosting.Vst3/Vst3Abi.cs`
- Modify: `src/MidiSharp.Hosting.Vst3/Vst3Plugin.cs`
- Modify: `src/MidiSharp.Hosting.Vst3/Vst3PlugFrame.cs`
- Test: `tests/MidiSharp.Hosting.Tests/Vst3WindowsEditorTests.cs`

**Interfaces:**
- Consumes: `IPluginGui` (in `MidiSharp.Hosting`), `EditorWindow.Open` / `INativeEditorWindow.EmbeddedChildCount` (Plan A), `Vst3Format`.
- Produces: `Vst3Plugin` now returns true from `IsApiSupported("win32", false)` / `SetParent("win32", hwnd)` for an editor-capable VST3 plugin on Windows.

- [ ] **Step 1: Write the failing test**

Create `tests/MidiSharp.Hosting.Tests/Vst3WindowsEditorTests.cs`:

```csharp
using System;
using System.Linq;
using System.Threading;
using MidiSharp.Hosting;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.Vst3;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Live Win32 embed of a REAL VST3 plugin's editor (u-he Podolski/Protoverb): confirms the adapter accepts
/// the "win32" window API ("HWND") and the plugin parents a child window into our host HWND. Self-skips off
/// Windows, on a non-interactive desktop, or when no target plugin is installed.
/// </summary>
[Collection("EditorWindows")]
public sealed class Vst3WindowsEditorTests
{
    private static readonly AudioConfig Config = new(48000, 512, ChannelCount: 2);
    private readonly Vst3Format _format = new();

    // A real, editor-bearing VST3 to target by name (well-behaved commercial plugins; loaded in-process).
    private PluginDescriptor? FindTarget() => _format.Scan(_format.DefaultSearchPaths)
        .FirstOrDefault(p => p.Name.Contains("Podolski", StringComparison.OrdinalIgnoreCase)
                          || p.Name.Contains("Protoverb", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Embeds_a_real_vst3_editor_in_a_native_window()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");
        var desc = FindTarget();
        Assert.SkipWhen(desc == null, "no target VST3 plugin (Podolski/Protoverb) installed.");

        using var plugin = _format.Load(desc!, Config);
        Assert.True(plugin.Gui is { HasEditor: true }, "the target VST3 should expose an editor.");
        Assert.True(plugin.Gui!.IsApiSupported("win32", floating: false), "the VST3 editor should support win32 (HWND).");

        using var window = EditorWindow.Open(plugin.Gui, "VST3 win32 editor test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor window should open (error: {window.Error}).");

        uint children = 0;
        for (var i = 0; i < 40 && children == 0; i++) { children = window.EmbeddedChildCount; if (children == 0) Thread.Sleep(50); }
        Assert.True(children >= 1, "the VST3 plugin should have embedded a child window via attached(HWND).");
        window.Close();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~Vst3WindowsEditorTests"`
Expected: FAIL on the `IsApiSupported("win32", ...)` assertion (the adapter still rejects everything but `"x11"`). (If Podolski/Protoverb are absent it would SKIP — confirm they are installed first via a quick scan; they are in `C:\Program Files\Common Files\VST3`.)

- [ ] **Step 3: Add the `HWND` platform-type constant**

In `src/MidiSharp.Hosting.Vst3/Vst3Abi.cs`, add directly after the `PlatformTypeX11` line (currently line 29):

```csharp
    public const string PlatformTypeHwnd = "HWND";
```

- [ ] **Step 4: Map the window API to a platform type in `Vst3Plugin`**

In `src/MidiSharp.Hosting.Vst3/Vst3Plugin.cs`, replace `IsApiSupported`:

```csharp
    bool IPluginGui.IsApiSupported(string windowApi, bool floating)
    {
        EnsureView();
        if (_view == null || windowApi != "x11") return false;
        Span<byte> t = stackalloc byte[20];
        AsciiZ(PlatformTypeX11, t);
        fixed (byte* p = t) return Ok(View->IsPlatformTypeSupported(_view, p));
    }
```

with:

```csharp
    bool IPluginGui.IsApiSupported(string windowApi, bool floating)
    {
        EnsureView();
        var platformType = PlatformTypeFor(windowApi);
        if (_view == null || platformType == null) return false;
        Span<byte> t = stackalloc byte[20];
        AsciiZ(platformType, t);
        fixed (byte* p = t) return Ok(View->IsPlatformTypeSupported(_view, p));
    }
```

and replace `SetParent`:

```csharp
    bool IPluginGui.SetParent(string windowApi, ulong windowHandle)
    {
        if (_view == null || windowApi != "x11") return false;
        Span<byte> t = stackalloc byte[20];
        AsciiZ(PlatformTypeX11, t);
        fixed (byte* p = t) return Ok(View->Attached(_view, (void*)(nuint)windowHandle, p));
    }
```

with:

```csharp
    bool IPluginGui.SetParent(string windowApi, ulong windowHandle)
    {
        var platformType = PlatformTypeFor(windowApi);
        if (_view == null || platformType == null) return false;
        Span<byte> t = stackalloc byte[20];
        AsciiZ(platformType, t);
        fixed (byte* p = t) return Ok(View->Attached(_view, (void*)(nuint)windowHandle, p));
    }
```

Then add this helper next to them (e.g. directly above `IsApiSupported`):

```csharp
    // Map our window-API name to the VST3 platform-type string the view expects: X11 on Linux, HWND on
    // Windows. Cocoa ("NSView") slots in when a macOS backend lands. Null => unsupported here.
    private static string? PlatformTypeFor(string windowApi) => windowApi switch
    {
        "x11" => PlatformTypeX11,
        "win32" => PlatformTypeHwnd,
        _ => null,
    };
```

(`PlatformTypeX11` is 16 chars + NUL; `PlatformTypeHwnd` is 4 + NUL — both fit `stackalloc byte[20]`. `PlatformTypeHwnd` resolves via the existing `using static ...Vst3Abi;`.)

- [ ] **Step 5: Gate the Linux `IRunLoop` to Linux in the plug frame**

In `src/MidiSharp.Hosting.Vst3/Vst3PlugFrame.cs`, replace the body of `FrameQueryInterface`:

```csharp
    private static int FrameQueryInterface(void* self, byte* iid, void** obj)
    {
        var f = Self(self);
        if (IidEq(iid, IidPlugFrame) || IidEq(iid, IidFUnknown)) { *obj = self; return ResultOk; }
        if (IidEq(iid, IidRunLoop)) { *obj = f._runLoop; return ResultOk; }
        *obj = null; return NoInterface;
    }
```

with:

```csharp
    private static int FrameQueryInterface(void* self, byte* iid, void** obj)
    {
        var f = Self(self);
        if (IidEq(iid, IidPlugFrame) || IidEq(iid, IidFUnknown)) { *obj = self; return ResultOk; }
        // Steinberg::Linux::IRunLoop is X11-only; on Windows the editor drives itself via the Win32 message
        // pump, so we must not advertise a run loop there.
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) && IidEq(iid, IidRunLoop)) { *obj = f._runLoop; return ResultOk; }
        *obj = null; return NoInterface;
    }
```

(`OperatingSystem` is in `System`, already imported in this file.)

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~Vst3WindowsEditorTests"`
Expected: PASS on Windows with Podolski/Protoverb installed — the real editor parents a child HWND.
If the editor opens but no child appears, the plugin may need a host service beyond the minimal `IPlugFrame` (e.g. content-scale); capture the symptom and report DONE_WITH_CONCERNS rather than guessing.

- [ ] **Step 7: Build the solution clean and commit**

Run: `dotnet build MidiSharp.slnx -c Debug` → expect 0 warnings, 0 errors.

```bash
git add src/MidiSharp.Hosting.Vst3/Vst3Abi.cs src/MidiSharp.Hosting.Vst3/Vst3Plugin.cs src/MidiSharp.Hosting.Vst3/Vst3PlugFrame.cs tests/MidiSharp.Hosting.Tests/Vst3WindowsEditorTests.cs
git commit -m "VST3: embed editors via HWND on Windows (Linux IRunLoop gated off)"
```

---

### Task 2: CLAP `win32` editor verification (clean-room fixture)

> **Deviation (2026-06-21, recorded in the SDD ledger):** the real CLAP plugin (ChowMultiTool) **hangs when
> hosted in-process** — its `gui.create`/`set_parent` blocks and `EditorWindow.Open` never returns (the
> out-of-process-sandbox scenario; VST3/Podolski embedded fine through the same backend). Per the user's
> decision, CLAP is verified with a **clean-room CLAP fixture** instead (using the official `clap.h` at
> `C:\Users\jorda\clap`). This task therefore authors the fixture + the shared fixture-build infra
> (`build-fixtures.ps1`, `WinFixtures.cs`, the `.gitignore` `out/` entry — Task 3's VST2 fixture then extends
> the same script). CLAP's adapter still needs **no change**; the fixture's `win32` editor embeds through the
> unchanged `ClapPlugin` pass-through. See the dispatched task brief for the concrete steps.

CLAP passes the window API straight to the plugin, so the adapter should need **no change** — prove it with a well-behaved clean-room fixture whose editor parents a child HWND. If `ClapPlugin`/`ClapHost` turn out to need a minimal win32 fix, make it and document why.

**Files:**
- Test: `tests/MidiSharp.Hosting.Tests/ClapWindowsEditorTests.cs`
- (Only if the test fails) Modify: `src/MidiSharp.Hosting.Clap/ClapPlugin.cs` and/or `ClapHost.cs`.

**Interfaces:**
- Consumes: `ClapFormat`, `IPluginGui`, `EditorWindow.Open`, `INativeEditorWindow.EmbeddedChildCount`.
- Produces: confirmation that CLAP win32 editor embedding works unchanged (or the minimal fix if not).

- [ ] **Step 1: Write the test**

Create `tests/MidiSharp.Hosting.Tests/ClapWindowsEditorTests.cs`:

```csharp
using System;
using System.Linq;
using System.Threading;
using MidiSharp.Hosting;
using MidiSharp.Hosting.Clap;
using MidiSharp.Hosting.EditorHost;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Live Win32 embed of a REAL CLAP plugin's editor (ChowMultiTool): CLAP passes the window API straight
/// through, so this verifies the win32 path end-to-end with no adapter change. Self-skips off Windows, on a
/// non-interactive desktop, or when no editor-bearing CLAP plugin is installed.
/// </summary>
[Collection("EditorWindows")]
public sealed class ClapWindowsEditorTests
{
    private static readonly AudioConfig Config = new(48000, 512, ChannelCount: 2);
    private readonly ClapFormat _format = new();

    private IHostedPlugin? LoadEditorPlugin()
    {
        // Prefer ChowMultiTool; else the first CLAP plugin that exposes an editor.
        var descs = _format.Scan(_format.DefaultSearchPaths)
            .OrderByDescending(p => p.Name.Contains("Chow", StringComparison.OrdinalIgnoreCase));
        foreach (var d in descs)
        {
            IHostedPlugin p;
            try { p = _format.Load(d, Config); } catch (NotSupportedException) { continue; }
            if (p.Gui is { HasEditor: true }) return p;
            p.Dispose();
        }
        return null;
    }

    [Fact]
    public void Embeds_a_real_clap_editor_in_a_native_window()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");
        var plugin = LoadEditorPlugin();
        Assert.SkipWhen(plugin == null, "no editor-bearing CLAP plugin installed.");
        using var _ = plugin;

        Assert.True(plugin!.Gui!.IsApiSupported("win32", floating: false), "the CLAP editor should support win32.");

        using var window = EditorWindow.Open(plugin.Gui, "CLAP win32 editor test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor window should open (error: {window.Error}).");

        uint children = 0;
        for (var i = 0; i < 40 && children == 0; i++) { children = window.EmbeddedChildCount; if (children == 0) Thread.Sleep(50); }
        Assert.True(children >= 1, "the CLAP plugin should have embedded a child window via set_parent(win32).");
        window.Close();
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~ClapWindowsEditorTests"`
Expected: **PASS** on Windows with ChowMultiTool installed — confirming CLAP needs no adapter change. If it FAILS, diagnose: the most likely cause is `ClapPlugin.BindRunLoop`/`ClapHost` editor-context wiring assuming POSIX behaviour. Make the **minimal** change to `ClapPlugin.cs`/`ClapHost.cs` to fix it, re-run, and note exactly what changed and why in the report.

- [ ] **Step 3: Commit**

```bash
git add tests/MidiSharp.Hosting.Tests/ClapWindowsEditorTests.cs
# include ClapPlugin.cs / ClapHost.cs ONLY if a fix was required
git commit -m "CLAP: verify win32 editor embedding against a real plugin"
```

---

### Task 3: VST2 `win32` editor embedding + clean-room fixture

VST2 has no real plugin installed, so build a clean-room gain `.dll` whose editor creates a child HWND, then teach the adapter `"win32"` and verify the embed.

> **Note (updated):** the shared fixture-build infra — `tests/fixtures/win/build-fixtures.ps1`, `tests/MidiSharp.Hosting.Tests/WinFixtures.cs`, and the `.gitignore` `tests/fixtures/win/out/` entry — was created in Task 2 (the CLAP fixture). This task **extends** `build-fixtures.ps1` with a second clang line for the VST2 fixture and does NOT recreate `WinFixtures.cs`/`.gitignore`. See the dispatched task brief for the exact, adjusted steps.

**Files:**
- Create: `tests/fixtures/win/vst2_gain_fixture.c`
- Create: `tests/fixtures/win/build-fixtures.ps1`
- Create: `tests/MidiSharp.Hosting.Tests/WinFixtures.cs`
- Modify: `src/MidiSharp.Hosting.Vst2/Vst2Plugin.cs`
- Modify: `.gitignore`
- Test: `tests/MidiSharp.Hosting.Tests/Vst2WindowsEditorTests.cs`

**Interfaces:**
- Consumes: `Vst2Format`, `IPluginGui`, `EditorWindow.Open`, `INativeEditorWindow.EmbeddedChildCount`.
- Produces: `Vst2Plugin` accepts `"win32"`; `WinFixtures.Dir` (the built-fixture directory for tests).

- [ ] **Step 1: Write the clean-room VST2 fixture**

Create `tests/fixtures/win/vst2_gain_fixture.c`:

```c
/* Clean-room VST2 gain effect with a Win32 (HWND) editor child. No Steinberg SDK headers — the AEffect
 * ABI is transcribed from MidiSharp's host-side Vst2Abi.cs. Name "MidiSharp VST2 Gain", uniqueID 'MsG2',
 * one "Gain" param (0..1 normalized -> x0..x2), 300x200 editor. Built by build-fixtures.ps1 (clang). */
#include <windows.h>
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

static float g_gain = 0.5f;                 /* normalized; 0.5 -> x1 (range x0..x2) */
static ERect g_rect = { 0, 0, 200, 300 };   /* 300 wide x 200 tall */
static HWND g_child = NULL;
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
            g_child = CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE,
                0, 0, g_rect.right - g_rect.left, g_rect.bottom - g_rect.top,
                (HWND)ptr, NULL, NULL, NULL);
            return g_child ? 1 : 0;
        }
        case effEditClose: if (g_child) { DestroyWindow(g_child); g_child = NULL; } return 1;
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

__declspec(dllexport) AEffect* VSTPluginMain(AudioMasterFn host) {
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

- [ ] **Step 2: Write the build script**

Create `tests/fixtures/win/build-fixtures.ps1`:

```powershell
# Builds the Windows clean-room native plugin fixtures into ./out (git-ignored).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $root "out"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$clang = (Get-Command clang -ErrorAction SilentlyContinue)?.Source
if (-not $clang) { if (Test-Path "C:\clang\bin\clang.exe") { $clang = "C:\clang\bin\clang.exe" } else { throw "clang not found (PATH or C:\clang\bin)" } }

& $clang -shared -O2 -o (Join-Path $out "midisharp_gain_vst2.dll") (Join-Path $root "vst2_gain_fixture.c") -luser32
if ($LASTEXITCODE -ne 0) { throw "clang failed ($LASTEXITCODE)" }
Write-Host "Built: $(Join-Path $out 'midisharp_gain_vst2.dll')"
```

- [ ] **Step 3: Ignore the fixture output and build the fixture**

Append to `.gitignore`:

```
tests/fixtures/win/out/
```

Run: `pwsh tests/fixtures/win/build-fixtures.ps1`
Expected: prints `Built: ...\out\midisharp_gain_vst2.dll`. Confirm the DLL exists.

- [ ] **Step 4: Write the fixture-path helper**

Create `tests/MidiSharp.Hosting.Tests/WinFixtures.cs`:

```csharp
using System;
using System.IO;

namespace MidiSharp.Hosting.Tests;

/// <summary>Locates the built Windows native test fixtures (see tests/fixtures/win/build-fixtures.ps1).
/// Override with the MIDISHARP_WIN_FIXTURES env var; otherwise resolved relative to the test assembly.</summary>
internal static class WinFixtures
{
    public static string Dir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("MIDISHARP_WIN_FIXTURES");
            if (!string.IsNullOrEmpty(env)) return env;
            // bin/Debug/net10.0 -> repo root is five levels up, then tests/fixtures/win/out.
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "tests", "fixtures", "win", "out"));
        }
    }

    public static bool Available => Directory.Exists(Dir);
}
```

- [ ] **Step 5: Write the failing VST2 editor test**

Create `tests/MidiSharp.Hosting.Tests/Vst2WindowsEditorTests.cs`:

```csharp
using System;
using System.Linq;
using System.Threading;
using MidiSharp.Hosting;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.Vst2;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Live Win32 embed of the clean-room VST2 gain fixture's editor (effEditOpen creates a child HWND).
/// Self-skips off Windows, on a non-interactive desktop, or when the fixture hasn't been built
/// (tests/fixtures/win/build-fixtures.ps1).
/// </summary>
[Collection("EditorWindows")]
public sealed class Vst2WindowsEditorTests
{
    private static readonly AudioConfig Config = new(48000, 512, ChannelCount: 2);
    private readonly Vst2Format _format = new();

    private IHostedPlugin? LoadFixture()
    {
        if (!WinFixtures.Available) return null;
        var d = _format.Scan([WinFixtures.Dir]).FirstOrDefault(p => p.Name == "MidiSharp VST2 Gain");
        return d == null ? null : _format.Load(d, Config);
    }

    [Fact]
    public void Embeds_the_vst2_fixture_editor_in_a_native_window()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");
        var plugin = LoadFixture();
        Assert.SkipWhen(plugin == null, "VST2 win32 fixture not built.");
        using var _ = plugin;

        var gui = plugin!.Gui;
        Assert.NotNull(gui);
        Assert.True(gui!.HasEditor);
        Assert.True(gui.IsApiSupported("win32", floating: false), "the fixture editor should support win32.");
        Assert.True(gui.TryGetSize(out var w, out var h));
        Assert.Equal(300, w);
        Assert.Equal(200, h);

        using var window = EditorWindow.Open(gui, "VST2 win32 editor test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor window should open (error: {window.Error}).");

        uint children = 0;
        for (var i = 0; i < 20 && children == 0; i++) { children = window.EmbeddedChildCount; if (children == 0) Thread.Sleep(50); }
        Assert.True(children >= 1, "effEditOpen should have created a child window in the host window.");
        window.Close();
    }
}
```

- [ ] **Step 6: Run the test to verify it fails**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~Vst2WindowsEditorTests"`
Expected: FAIL on `IsApiSupported("win32", ...)` — the adapter still gates on `"x11"`.

- [ ] **Step 7: Accept `win32` in the VST2 adapter**

In `src/MidiSharp.Hosting.Vst2/Vst2Plugin.cs`, replace:

```csharp
    bool IPluginGui.IsApiSupported(string windowApi, bool floating)
        => (_eff->Flags & FlagsHasEditor) != 0 && windowApi == "x11";   // VST2 on Linux embeds via X11
```

with:

```csharp
    bool IPluginGui.IsApiSupported(string windowApi, bool floating)
        => (_eff->Flags & FlagsHasEditor) != 0 && windowApi is "x11" or "win32";   // X11 on Linux, HWND on Windows
```

and replace:

```csharp
    bool IPluginGui.SetParent(string windowApi, ulong windowHandle)
    {
        if ((_eff->Flags & FlagsHasEditor) == 0 || windowApi != "x11") return false;
        _eff->Dispatcher(_eff, EffEditOpen, 0, IntPtr.Zero, (void*)(nuint)windowHandle, 0);
        _editorOpen = true;
        return true;
    }
```

with:

```csharp
    bool IPluginGui.SetParent(string windowApi, ulong windowHandle)
    {
        if ((_eff->Flags & FlagsHasEditor) == 0 || windowApi is not ("x11" or "win32")) return false;
        _eff->Dispatcher(_eff, EffEditOpen, 0, IntPtr.Zero, (void*)(nuint)windowHandle, 0);   // ptr = parent HWND on Windows
        _editorOpen = true;
        return true;
    }
```

- [ ] **Step 8: Run the test to verify it passes**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~Vst2WindowsEditorTests"`
Expected: PASS on Windows — the fixture's `effEditOpen` parents a child HWND.

- [ ] **Step 9: Build clean, run the full Hosting suite, and commit**

Run: `dotnet build MidiSharp.slnx -c Debug` → 0 warnings, 0 errors.
Run: `dotnet test tests/MidiSharp.Hosting.Tests` → all pass; X11/sandbox/no-fixture tests SKIP; 0 failed.

```bash
git add tests/fixtures/win/vst2_gain_fixture.c tests/fixtures/win/build-fixtures.ps1 tests/MidiSharp.Hosting.Tests/WinFixtures.cs tests/MidiSharp.Hosting.Tests/Vst2WindowsEditorTests.cs src/MidiSharp.Hosting.Vst2/Vst2Plugin.cs .gitignore
git commit -m "VST2: embed editors via HWND on Windows + clean-room win32 fixture"
```

---

## Self-Review

**Spec coverage (§5.2, §6, §7):**
- §5.2 CLAP no change → Task 2 (verified against a real plugin). ✓
- §5.2 VST2 accept win32 + effEditOpen HWND → Task 3. ✓
- §5.2 VST3 add HWND, accept win32, gate Linux IRunLoop → Task 1. ✓
- §6 fixtures: VST2 clean-room `.dll` + build script + out-dir ignore → Task 3. CLAP/VST3 fixtures intentionally replaced by real-plugin verification (decision recorded with the user; real plugins are stronger proof and present on this machine). ✓
- §7 tests: per-format win32 editor tests, self-skipping, `[Collection("EditorWindows")]` → Tasks 1-3. The `ClapGuiTests.cs:35` per-OS flip is omitted: that test is bound to the Linux `midisharp.test.gui` fixture (absent on Windows → it skips), so it needs no Windows change; the real-plugin `ClapWindowsEditorTests` covers win32. ✓

**Placeholder scan:** No TBD/TODO. Every code step shows complete code; every run step shows the command + expected result. The only conditional code (a CLAP fix in Task 2) is explicitly gated on a measured test failure with a diagnosis pointer — not a placeholder. ✓

**Type consistency:** `PlatformTypeHwnd` defined in Task 1 (Vst3Abi) and used in `PlatformTypeFor` (Vst3Plugin) same task. `PlatformTypeFor` returns `string?`; callers null-check. `WinFixtures.Dir`/`.Available` defined in Task 3 Step 4, used in Step 5. VST2 fixture name "MidiSharp VST2 Gain" matches the test's `Scan(...).FirstOrDefault(p => p.Name == "MidiSharp VST2 Gain")`. Editor size 300×200 in the fixture matches the test's `Assert.Equal(300, w); Assert.Equal(200, h)`. All `IsApiSupported`/`SetParent` edits keep `"x11"` working (additive `or "win32"`). ✓

**Risk note for the executor:** Tasks 1-2 load **real** plugins in-process; u-he and Chowdhury plugins are well-behaved, but a real plugin's editor may want a host service the minimal `IPlugFrame`/`clap_host` doesn't provide. If an editor opens a top-level window but embeds no child, that's the signal — report it with the observed behavior rather than forcing the assertion.
