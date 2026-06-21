# Windows Plugin-Editor Hosting — Design

**Goal:** open native plugin editor windows on **Windows (Win32)** at parity with the existing **X11**
(Linux) backend, for all three GUI-capable formats — **CLAP, VST2, VST3** — both in-process and through the
out-of-process sandbox worker.

**Status:** approved 2026-06-21. macOS (Cocoa) is a separate follow-up the author will do on a Mac; this
design keeps the Win32 backend a clean template for it.

This is the Windows half of Phase 7 ("Native plugin GUIs") from
[`docs/plugin-hosting-plan.md`](../../plugin-hosting-plan.md), which lists Win32/Cocoa windowing as the
remaining work after X11 shipped 2026-06-20.

---

## 1. Where it plugs in

The editor windowing already sits behind a per-OS seam in `MidiSharp.Hosting.EditorHost`:

- `INativeEditorWindow` / `IEditorPlatform` (`IEditorPlatform.cs`) — a host window abstracted away from any
  one windowing system, and a factory that creates them.
- `EditorPlatform.Current` → `Select()` returns `X11Platform` on Linux/FreeBSD and an `UnsupportedPlatform`
  everywhere else. The code comment already reserves the slot: *"Windows (Win32) and macOS (Cocoa) backends
  slot in here."*
- `EditorSession` (`EditorSession.cs`) sequences the **format-agnostic** embed against an
  `INativeEditorWindow` and never touches X11/Win32 directly:
  `CreateWindow → BindRunLoop → gui.Create(WindowApi) → SetScale → Resize → gui.SetParent(WindowApi, Handle)
  → Map → CompleteEmbed → gui.Show → Resize → RunLoop.RegisterTimer(idle)`.
- `IPluginGui` (`MidiSharp.Hosting/IPluginGui.cs`) is the format-agnostic editor lifecycle each adapter
  implements; `IEditorRunLoop` (same file) is the host-services interface a plugin registers fds/timers with.

Adding Windows support means: (a) one new backend implementing `IEditorPlatform` + `INativeEditorWindow` +
a Win32 run loop, registered in `Select()`; and (b) per-format HWND embedding, because the format adapters'
GUI paths are X11-hardcoded today.

## 2. Guiding decisions (locked)

- **Pure `user32`/`gdi32` P/Invoke**, mirroring the pure-Xlib X11 backend. No WinForms/WPF/WinUI — the
  hosting libraries stay dependency-free and net10.0, and the Win32 backend stays a faithful structural
  parallel of the X11 one.
- **Run loop = `MsgWaitForMultipleObjectsEx(timeout = nearest due timer)` → drain messages → fire due timers
  → run posted work.** This is the faithful parallel of the X11 `poll(timeout)` → drain → timers → posted
  structure, keeps CLAP `clap.timer-support` working, and does not busy-spin. `RegisterFd`/`UnregisterFd`
  are no-ops on Windows (CLAP `clap.posix-fd-support` is POSIX-only by spec; Windows plugins do not use it).
- **Clean-room C fixtures, sources checked into the repo** under `tests/fixtures/win/`, built by a script
  (clang). The Linux fixtures are not in the repo (the live tests self-skip without them); checking the
  Win32 fixture *sources* in makes the measured parity reproducible — an improvement over the ad-hoc Linux
  situation, not a regression.
- **No native shim, no vendored SDK headers.** Same clean-room discipline as the rest of the hosting stack:
  CLAP public headers, a clean-room VST2 `AEffect`, and `vst3_c_api.h`-shaped fixtures.

## 3. Invariants (regression guards — write these as tests)

1. **The audio path is untouched.** Editor work is main-thread only, off the audio callback; no synth or
   `Process` change.
2. **The X11 backend and all platform-agnostic code are unchanged in behavior** — `EditorSession`,
   `EditorWindow`, `Worker/Program.cs`, `SandboxedPlugin`. Out-of-process editors light up on Windows purely
   because `Select()` now returns a Win32 backend.
3. **One UI thread.** Every editor call (create/parent/show/idle/destroy) and every message-pump iteration
   runs on the single thread that owns the editor (already enforced by `EditorSession`/`EditorWindow`).
4. **Dependency-free.** The Win32 backend adds only `user32`/`gdi32` P/Invoke — no new package references.

## 4. Architecture — what does NOT change

`EditorSession`, `EditorWindow`, the out-of-process `Worker` (`Program.cs` `CmdOpenEditor`/`CmdCloseEditor`,
which drives `EditorSession.Open` on the plugin's creation thread), and `SandboxedPlugin`
(`OpenEditor`/`CloseEditor` proxy) are all windowing-agnostic. The server endpoints
(`/api/plugins/editor/open|close`) and `PluginInfoDto.HasEditor` are unchanged. Plugin discovery is already
Windows-aware (CLAP searches `%CommonProgramFiles%\CLAP` + LocalAppData; VST3 resolves the
`Contents/x86_64-win/<name>.vst3` bundle layout; VST2 uses `*.dll`).

## 5. Components

### 5.1 New Win32 backend (`MidiSharp.Hosting.EditorHost`) — three files mirroring the X11 trio

**`Win32.cs`** — the `user32`/`gdi32` P/Invoke slice (Unicode `W` entry points throughout):
`RegisterClassExW`, `CreateWindowExW`, `DestroyWindow`, `ShowWindow`, `SetWindowPos`/`MoveWindow`,
`PeekMessageW` + `TranslateMessage` + `DispatchMessageW`, `MsgWaitForMultipleObjectsEx`, `DefWindowProcW`,
`EnumChildWindows` / `GetWindow(GW_CHILD)`, `GetClientRect`, `SetProcessDpiAwarenessContext`, and a black
`CreateSolidBrush`/stock brush for the window-class background (matches the X11 black-background fix that
avoids bright compositing seams behind a dark editor).

**`Win32EditorWindow : INativeEditorWindow`** — a top-level `WS_OVERLAPPEDWINDOW` host window:
- `WindowApi => "win32"`; `Handle =>` the HWND as `ulong`.
- A `WndProc` (kept alive against GC via a static/instance delegate field) that latches `_shouldClose` on
  `WM_CLOSE` and otherwise calls `DefWindowProcW`.
- `Resize(w,h)` sizes the host window's client area to `w×h` and the embedded child to match
  (`SetWindowPos` on `GetWindow(GW_CHILD)`).
- `Map()` = `ShowWindow(SW_SHOW)`; `CompleteEmbed()` = no-op (no XEMBED handshake on Win32).
- `EmbeddedChildCount` via `EnumChildWindows` (≥1 once the plugin parented its editor — the verification
  hook the tests use).
- `PumpOnce(maxWaitMs)` delegates to `Win32RunLoop.Pump`.
- `Dispose()` destroys the window and unregisters the class.

**`Win32RunLoop : IEditorRunLoop`** — timers + posted work identical in shape to `EditorRunLoop`, but:
- `Pump`: compute `timeout = min(maxWaitMs, nearest due timer)`; `MsgWaitForMultipleObjectsEx(0 handles,
  timeout, QS_ALLINPUT, MWMO_INPUTAVAILABLE)`; drain with `PeekMessageW(PM_REMOVE)` →
  `TranslateMessage`/`DispatchMessageW`; fire due timers (snapshot first, so a callback may
  register/unregister); drain posted work. Mirrors the X11 `Pump` exactly.
- `RegisterFd`/`UnregisterFd`: no-op. `RegisterTimer`/`UnregisterTimer`/`Post`: same as X11.

**`Win32Platform : IEditorPlatform`** (in `Win32.cs` or its own file) — `IsAvailable` is true on Windows
with an interactive desktop, detected once (cached) by creating a hidden message-only/offscreen test window
and checking it succeeded; false on a non-interactive session (Server Core, a service) so the live tests
self-skip. `CreateWindow` returns a `Win32EditorWindow` or null.

**Wiring:** one line in `EditorPlatform.Select()` — `if (OperatingSystem.IsWindows()) return new Win32Platform();`
ahead of the `UnsupportedPlatform` fallback.

### 5.2 Per-format HWND embedding (the only engine-side changes)

**CLAP (`MidiSharp.Hosting.Clap/ClapPlugin.cs`)** — passes `windowApi`/handle straight to the plugin and
already has `WindowApiWin32` and a pointer-sized `clap_window` union (`ClapAbi.cs:26,206-212`). Expected:
**no code change.** Confirmed by the win32 fixture test. `BindRunLoop` stays; only `clap.timer-support`
(not `posix-fd`) is exercised on Windows.

**VST2 (`MidiSharp.Hosting.Vst2/Vst2Plugin.cs`)** — `IsApiSupported` (`:161`) and `SetParent` (`:178`) gate
on `windowApi == "x11"`. Accept `"win32"` and hand the HWND to `effEditOpen` (`EffEditOpen = 14`,
`Vst2Abi.cs:29`). `effEditIdle` is already driven by `IPluginGui.Idle()`.

**VST3 (`MidiSharp.Hosting.Vst3/Vst3Plugin.cs` + `Vst3Abi.cs` + `Vst3PlugFrame.cs`)** — the bulk of the
format work:
- Add `PlatformTypeHWND = "HWND"` (`Vst3Abi.cs`, alongside `PlatformTypeX11 = "X11EmbedWindowID"` at `:29`).
- `IsApiSupported` (`:338`) and `SetParent` (`:367`): accept `windowApi == "win32"` → use `"HWND"` for
  `IsPlatformTypeSupported`/`attached`.
- **Gate the Linux `IRunLoop` to Linux only.** On Windows the VST3 editor self-drives via the Win32 message
  pump; the host must not offer `Steinberg::Linux::IRunLoop`. `Vst3PlugFrame`'s `resizeView` callback stays
  (cross-platform); `getRunLoop`/`Linux::IRunLoop` query is answered only on Linux. Verify the plug frame's
  `queryInterface` returns `kNoInterface` for the run-loop IID on Windows.

## 6. Fixtures (`tests/fixtures/win/`)

Three clean-room C sources + `build-fixtures.ps1` (clang), each a minimal **gain** plugin whose **editor is a
real `WS_CHILD` HWND** (a solid-color child window) created under the host HWND on parent/attach:

| Fixture | Output | Editor | Notes |
|---|---|---|---|
| `clap_gui_fixture.c` | `midisharp_gui.clap` (DLL) | `clap.gui` win32 + `clap.timer-support` | id `midisharp.test.gui`; reports 320×240; counts `on_timer` into the first 8 bytes of `clap.state` (proves the run loop drives it). A second id `midisharp.test.gain` with no editor mirrors the Linux pair. |
| `vst2_gui_fixture.c` | `midisharp_gui_vst2.dll` | `effEditOpen`/`effEditGetRect`/`effEditClose` | clean-room `AEffect`; child HWND on `effEditOpen(parent)`. |
| `vst3_gui_fixture.c` | `MidiSharpGui.vst3/Contents/x86_64-win/MidiSharpGui.vst3` | `IPlugView` `"HWND"` | `vst3_c_api.h`-shaped; single-component; `attached(hwnd,"HWND")` creates the child. Windows bundle layout. |

- **Sources are checked in; build outputs are git-ignored.** `build-fixtures.ps1` resolves clang
  (`C:\clang\bin\clang` or PATH), compiles each to its DLL/extension and lays out the VST3 bundle, writing
  to `tests/fixtures/win/out/`. Add `tests/fixtures/win/out/` to `.gitignore` (the repo's existing `bin/`
  rule does not cover it).
- **Discovery:** a `WinFixtures` test helper returns the output dir (overridable via env
  `MIDISHARP_WIN_FIXTURES`); the win32 live tests scan that dir (so tests are hermetic and need no
  system-wide plugin install / elevation). Default-path discovery still works if a fixture is installed
  there.

## 7. Tests (`tests/MidiSharp.Hosting.Tests`)

Mirror the Linux editor/gui tests; reuse the `EditorWindows` xUnit collection so editor tests don't run in
parallel; self-skip via `EditorPlatform.Current.IsAvailable` (headless/Server Core session) and when a
fixture is absent (`Assert.SkipWhen`, the existing pattern).

- **`Win32EditorWindowTests` (no fixture, no toolchain):** a managed `FakeEditorGui : IPluginGui` (sibling of
  `FakeGainPlugin`) that creates a `WS_CHILD` HWND under the handle it's given in `SetParent`. Drives the
  real `Win32Platform`/`Win32EditorWindow`/`Win32RunLoop`: open → embed → `EmbeddedChildCount ≥ 1` → `Resize`
  changes the child's client rect → `WM_CLOSE` latches `ShouldClose` → `Close`. This is the backend's
  always-on regression guard.
- **Per-format win32 tests** against the fixtures (CLAP/VST2/VST3): `IsApiSupported("win32")` true /
  `"x11"` false; embedding produces a child window; the CLAP fixture's timer ticks climb (run loop drives
  the editor).
- **Make `ClapGuiTests.cs:35`'s `IsApiSupported` assertion per-OS** (win32 supported on Windows, x11 on
  Linux) rather than hardcoded.

## 8. Build / toolchain

clang is on PATH (`C:\clang\bin\clang`); MSVC (VS Community 2026) is also present as a fallback. The fixture
script uses clang for a single, simple invocation per fixture. No change to the .NET build; the new backend
files compile into the existing `MidiSharp.Hosting.EditorHost` (net10.0) assembly. Target build result:
0 warnings, 0 errors; existing Linux tests unaffected.

## 9. Correctness & threading notes

- Per-process DPI awareness set once (e.g. `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`), guarded so
  it is harmless if the host already set it.
- The `WndProc` delegate is rooted for the window's lifetime (GCHandle or a field) — a collected delegate
  would crash the message pump.
- Window-class name is unique per process (suffix with a counter/PID) to avoid `RegisterClassExW` collisions
  across reopen cycles; unregister on dispose.
- Sizes are physical pixels (matches `IPluginGui` remark for X11/Win32).

## 10. Acceptance gate (measured, mirroring the Linux gate)

1. **Backend (always runs):** `Win32EditorWindowTests` embeds the managed fake's child, resizes it, and
   closes via `WM_CLOSE` — `EmbeddedChildCount ≥ 1`, child rect tracks `Resize`, `ShouldClose` latches.
2. **CLAP:** the win32 gui fixture reports an editor, `IsApiSupported("win32")` is true, embedding produces a
   child, and the editor's `clap.timer-support` tick count climbs under the host run loop.
3. **VST2:** the win32 gui fixture embeds a child via `effEditOpen`; `IsApiSupported("win32")` true.
4. **VST3:** the win32 gui fixture embeds a child via `attached(hwnd,"HWND")`; `IsApiSupported("win32")`
   true; the separate-controller and IBStream paths are unaffected.
5. Solution builds 0/0; the X11 backend and all platform-agnostic code are behaviorally unchanged.

## 11. Out of scope

- **macOS / Cocoa** — the author's separate Mac task; it slots into the same `Select()` seam, and this Win32
  backend is its template.
- **The web mixing console** — OS-agnostic HTML/JS, unchanged.
- **Bundling `MidiSharp.Hosting.EditorHost` into the `MidiSharp.Player` package** — hosting remains a
  sample-server capability; packaging is a separate decision.

## 12. Open items to verify during implementation (not blockers)

- Confirm CLAP truly needs zero changes (the fixture test is the proof).
- Confirm `Vst3PlugFrame`'s `queryInterface` cleanly declines `Linux::IRunLoop` on Windows and that the
  plug-frame `resizeView` path stays wired.
- Pick the no-elevation discovery path for the win32 fixtures (repo-local `out/` dir via the test helper is
  the default; system plugin dirs optional).
