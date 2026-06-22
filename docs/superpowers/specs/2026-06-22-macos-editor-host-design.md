# macOS Plugin-Editor Hosting — Design

**Goal:** open native plugin editor windows on **macOS (Cocoa)** at parity with the existing **X11** (Linux)
and **Win32** (Windows) backends, for the three GUI-capable formats — **CLAP, VST2, VST3** — both in-process
and through the out-of-process sandbox worker.

**Status:** draft 2026-06-22. This is the macOS half of Phase 7 ("Native plugin GUIs") from
[`docs/plugin-hosting-plan.md`](../../plugin-hosting-plan.md), and the last platform the plan lists as
remaining (Win32/Cocoa windowing). The Win32 design ([`2026-06-21-windows-editor-host-design.md`](2026-06-21-windows-editor-host-design.md))
was written to be "a clean template" for this; this document is that follow-up. **AU (Audio Units) is out of
scope** — it is a separate macOS-only *format* adapter, still parked in the master plan.

---

## 1. Where it plugs in

The editor windowing already sits behind a per-OS seam in `MidiSharp.Hosting.EditorHost`, and Windows just
travelled it:

- `INativeEditorWindow` / `IEditorPlatform` (`IEditorPlatform.cs`) — a host window abstracted away from any one
  windowing system, and a factory that creates them.
- `EditorPlatform.Current` → `Select()` returns `X11Platform` (Linux/FreeBSD), `Win32Platform` (Windows), and
  an `UnsupportedPlatform` everywhere else. The code comment already reserves the slot: *"macOS (Cocoa) backend
  slots in here."*
- `EditorSession` (`EditorSession.cs`) sequences the **format-agnostic** embed against an `INativeEditorWindow`
  and never touches X11/Win32/Cocoa directly:
  `CreateWindow → BindRunLoop → gui.Create(WindowApi) → SetScale → Resize → gui.SetParent(WindowApi, Handle)
  → Map → CompleteEmbed → gui.Show → Resize → RunLoop.RegisterTimer(idle)`.
- `IPluginGui` (`MidiSharp.Hosting/IPluginGui.cs`) is the format-agnostic editor lifecycle each adapter
  implements; `IEditorRunLoop` (same file) is the host-services interface a plugin registers fds/timers with.

Adding macOS support means: (a) one new backend implementing `IEditorPlatform` + `INativeEditorWindow` + a
Cocoa run loop, registered in `Select()`; and (b) per-format **NSView** embedding, because the format adapters'
GUI paths only know `"x11"`/`"win32"` today.

## 2. Guiding decisions (locked)

- **Pure Objective-C runtime + AppKit/Foundation via `objc_msgSend`**, mirroring the pure-Xlib X11 backend and
  the pure-`user32`/`gdi32` Win32 backend. P/Invoke into `/usr/lib/libobjc.A.dylib` (`objc_getClass`,
  `sel_registerName`, `objc_msgSend`) and load the AppKit framework; build `NSWindow`/`NSView` by sending
  messages. **No Xamarin.Mac / MonoMac / MAUI / AppKit binding package** — the hosting libraries stay
  dependency-free and net10.0, and the Cocoa backend stays a faithful structural parallel of the other two.
- **arm64 only, for now.** On Apple Silicon `objc_msgSend` is uniform — a single entry point handles
  pointer-, scalar-, and struct-returning sends (an `NSRect` is four `double`s, an HFA returned in `v0–v3`),
  so there is **no `objc_msgSend_stret`** to special-case. x86_64 (which *would* need `_stret` for `NSRect`)
  is deferred; the dev machine and CI target are arm64. The backend self-reports unavailable on x86_64 rather
  than mis-marshalling.
- **Run loop = pump `NSApp` events bounded by the nearest due timer.** The faithful parallel of X11
  `poll(timeout) → drain → timers → posted` and Win32 `MsgWaitForMultipleObjectsEx(timeout) → drain → timers
  → posted`: `[NSApp nextEventMatchingMask:NSEventMaskAny untilDate:(now+timeout) inMode:NSDefaultRunLoopMode
  dequeue:YES]` (blocks up to `timeout` for the first event), then drain the rest with `untilDate:distantPast`,
  `[NSApp sendEvent:]` each; then fire due timers and run posted work. Keeps CLAP `clap.timer-support` working
  and does not busy-spin.
- **`RegisterFd`/`UnregisterFd` are no-ops initially**, as on Win32. CLAP `clap.posix-fd-support` *is* available
  on macOS (POSIX), but mac editors animate via `clap.timer-support` and VST2/VST3 self-drive through the
  `NSApp` loop; wiring plugin fds into the Cocoa run loop (`CFFileDescriptor`/`CFRunLoopSource`) is a documented
  deferral (§11), not a parity blocker for the formats we ship.
- **`ShouldClose` by polling `NSWindow.isVisible`, not a synthesized delegate.** Create the window with
  `setReleasedWhenClosed:NO`; after `makeKeyAndOrderFront:` it is visible, and the title-bar close button orders
  it out → `isVisible` goes false → `ShouldClose` latches. This avoids `objc_allocateClassPair`/`class_addMethod`
  to build an `NSWindowDelegate` at runtime — the host controls teardown either way (it parallels Win32 latching
  `WM_CLOSE`).
- **Black window background** (`NSColor.blackColor`), matching the X11/Win32 black-background fix that avoids
  bright compositing seams behind a dark editor.

## 3. Invariants (regression guards)

1. **The audio path is untouched.** Editor work is main-thread only, off the audio callback; no synth or
   `Process` change.
2. **The X11 and Win32 backends and all platform-agnostic code are unchanged in behavior** — `EditorSession`,
   `EditorWindow`, `Worker/Program.cs`, `SandboxedPlugin`. Out-of-process editors light up on macOS purely
   because `Select()` now returns a Cocoa backend.
3. **One UI thread — and on macOS it must be the process main thread.** Every editor call
   (create/parent/show/idle/destroy) and every `NSApp` pump runs on the single thread that owns the editor;
   AppKit requires that thread to be the main thread (see §5.3, the one place macOS genuinely diverges).
4. **Dependency-free.** The Cocoa backend adds only `libobjc` + system-framework P/Invoke — no new package
   references.

## 4. Architecture — what does NOT change

`EditorSession`, `EditorWindow`, the out-of-process `Worker` (`Program.cs` `CmdOpenEditor`/`CmdCloseEditor`,
which drives `EditorSession.Open` on the plugin's creation thread), and `SandboxedPlugin`
(`OpenEditor`/`CloseEditor` proxy) are all windowing-agnostic. The server endpoints
(`/api/plugins/editor/open|close`) and `PluginInfoDto.HasEditor` are unchanged. Plugin discovery is **already
macOS-aware**: CLAP searches `~/Library/Audio/Plug-Ins/CLAP` + `/Library/Audio/Plug-Ins/CLAP`
(`ClapFormat.cs:41`); VST3 resolves the `Contents/MacOS/<name>` bundle layout with no extension
(`Vst3Format.cs:84-87`); the memory-mapped sample loader already handles the `OSX` `madvise` path
(`MemoryMappedFileManager.cs:97-98`). **Re-verifying those existing mac paths actually run on this machine is a
tracked companion task** (build/test through `dotnet`, see §8) but requires no code change here.

## 5. Components

### 5.1 New Cocoa backend (`MidiSharp.Hosting.EditorHost`) — three files mirroring the X11/Win32 trio

**`Cocoa.cs`** — the Objective-C runtime + AppKit slice. Pure interop, no logic. Internal. Holds:
- `objc_getClass`, `sel_registerName`, and the family of `objc_msgSend` declarations (one C# method per
  distinct native signature, all `EntryPoint = "objc_msgSend"` into `/usr/lib/libobjc.A.dylib`): the
  `IntPtr`-returning forms (0–3 `IntPtr`/`nuint` args), a `bool`-returning form, the `CGRect`-arg form
  (`setFrame:`, `initWithContentRect:…`), and the `CGRect`-returning form (`frame`/`contentLayoutRect`).
- A `CGRect`/`NSRect` struct `{ double X, Y, Width, Height }` and `NSSize { double Width, Height }`.
- Class handles cached once (`NSApplication`, `NSWindow`, `NSView`, `NSColor`, `NSString`, `NSDate`,
  `NSScreen`, `NSAutoreleasePool`) and a small `Sel(name)` selector cache.
- Constants: style-mask bits (`Titled=1`, `Closable=2`, `Miniaturizable=4`, `Resizable=8`),
  `NSBackingStoreBuffered=2`, activation policies, autoresizing masks (`WidthSizable=2`, `HeightSizable=16`),
  `NSEventMaskAny`, and the `NSDefaultRunLoopMode` string.
- Helpers: `NSString(string)` → autoreleased `NSString`; `Utf8(IntPtr)` back; loads
  `/System/Library/Frameworks/AppKit.framework/AppKit` in a static ctor so the classes resolve.

**`CocoaEditorWindow : INativeEditorWindow`** — a top-level `NSWindow` whose content view is a plain `NSView`:
- `WindowApi => "cocoa"`; `Handle =>` the **content `NSView*`** as `ulong` (what CLAP `cocoa` / VST3 `NSView` /
  VST2 cocoa all parent into — *not* the `NSWindow`).
- `Open`: `[[NSWindow alloc] initWithContentRect:{0,0,w,h} styleMask:(Titled|Closable|Resizable)
  backing:Buffered defer:NO]`; `setReleasedWhenClosed:NO`; `setTitle:`; `setBackgroundColor:[NSColor blackColor]`;
  install a fresh `NSView` as `contentView` (autoresizes with the window).
- `Resize(w,h)`: `setContentSize:` and size the embedded subview to match (points; see §9 on Retina).
- `Map()`: `[win makeKeyAndOrderFront:nil]` + `[NSApp activateIgnoringOtherApps:YES]`.
- `CompleteEmbed()`: no-op (no XEMBED handshake on Cocoa).
- `EmbeddedChildCount`: `[[contentView subviews] count]` — ≥1 once the plugin added its view (the verification
  hook the gate uses).
- `ShouldClose`: `_opened && ![win isVisible]` (per §2).
- `PumpOnce(maxWaitMs)`: delegates to `CocoaRunLoop.Pump`, wrapped in an autorelease pool per iteration.
- `Dispose()`: `orderOut:` + `close` the window, release retained objects.

**`CocoaRunLoop : IEditorRunLoop`** — timers + posted work identical in shape to `EditorRunLoop`/`Win32RunLoop`,
but `Pump` drives the `NSApp` event queue: compute `timeout = min(maxWaitMs, nearest due timer)`; block up to
`timeout` on `nextEventMatchingMask:untilDate:inMode:dequeue:` and `sendEvent:` it; drain the rest with
`distantPast`; fire due timers (snapshot first); run posted work. `RegisterFd`/`UnregisterFd`: no-op (§2).
`RegisterTimer`/`UnregisterTimer`/`Post`: same as the others.

**`CocoaPlatform : IEditorPlatform`** — `IsAvailable` is true on **arm64 macOS with a window server**: ensure
`[NSApplication sharedApplication]` exists and its activation policy is set once (on the main thread), and check
`[[NSScreen screens] count] > 0` (zero screens = headless/SSH ⇒ false, so the live gate self-skips). False on
x86_64 (we don't ship `_stret`). `CreateWindow` returns a `CocoaEditorWindow` or null.

**Wiring:** one line in `EditorPlatform.Select()` —
`if (OperatingSystem.IsMacOS()) return new CocoaPlatform();` ahead of the `UnsupportedPlatform` fallback.

### 5.2 Per-format NSView embedding (the only engine-side changes)

**CLAP (`MidiSharp.Hosting.Clap/ClapPlugin.cs`)** — passes `windowApi`/handle straight to the plugin and
already has `WindowApiCocoa = "cocoa"` (`ClapAbi.cs:27`) and a pointer-sized `clap_window` union whose `Handle`
carries a `void*` for cocoa (`ClapAbi.cs:206-212`). Expected: **no code change.** Confirmed by the cocoa fixture.

**VST2 (`MidiSharp.Hosting.Vst2/Vst2Plugin.cs`)** — `IsApiSupported` (`:159-161`) and `SetParent` (`:175-178`)
gate on `windowApi is "x11" or "win32"`. Accept `"cocoa"` and hand the `NSView*` to `effEditOpen`
(`EffEditOpen = 14`, `Vst2Abi.cs:29`; the `ptr` is the parent view on Cocoa). `effEditIdle` is already driven by
`IPluginGui.Idle()`.

**VST3 (`MidiSharp.Hosting.Vst3/Vst3Plugin.cs` + `Vst3Abi.cs`)** — small:
- Add `PlatformTypeNsView = "NSView"` (`Vst3Abi.cs`, alongside `PlatformTypeX11 = "X11EmbedWindowID"` `:29` and
  `PlatformTypeHwnd = "HWND"` `:30`).
- `PlatformTypeFor` (`:336`): add `"cocoa" => PlatformTypeNsView`. `IsApiSupported`/`SetParent` then accept
  `"cocoa"` with no further change; the `stackalloc byte[20]` (`:348`) already fits `"NSView\0"` (7 bytes).
- **`Vst3PlugFrame` needs no change.** Its `IRunLoop` hand-out is already gated to Linux/FreeBSD only
  (`Vst3PlugFrame.cs:68`), which is correct: a VST3 editor on macOS (like Windows) self-drives via the OS run
  loop and must **not** be offered `Steinberg::Linux::IRunLoop`. macOS already falls through. Confirm
  `queryInterface` declines the run-loop IID on macOS (it will).

## 5.3 Threading — the one place macOS genuinely diverges

AppKit is **main-thread-only**: `NSWindow`/`NSView` must be created and messaged on the process main thread
(thread 0, the one that entered `main`), with an `NSApplication` initialized. The other two backends don't care
which thread owns the editor; macOS does. Consequences:

- **macOS editors are sandboxed-only by design — and that preserves crash isolation.**
  `MidiSharp.Hosting.Worker/Program.cs` runs its command loop — and therefore `EditorSession.Open`/`PumpOnce` —
  on the process **main thread** (top-level statements execute on the entry thread). So editors opened through
  the live server (the default, sandboxed path) are AppKit-correct with no change, and a misbehaving plugin —
  including its editor's native code — takes down only its isolated worker process, exactly as on Linux/Windows.
  **No in-process Cocoa editor path is built**; in-process hosting buys nothing for a GUI window and is the
  non-isolated mode by definition.
- **The pre-existing in-process branch fails safe instead of crashing.** `EditorWindow` (used by the shared
  `PlayerService` branch when the sandbox is off on Linux/Windows) runs its `EditorSession` on a **dedicated
  background thread** (`EditorWindow.cs:26`), which AppKit forbids. We don't rework `EditorWindow` (it stays
  correct on X11/Win32); instead the Cocoa backend **declines to create a window off the main thread**
  (`CocoaPlatform.CreateWindow` checks `pthread_main_np()` and returns null). The worker's main thread passes;
  the in-process background thread fails the guard, so that branch returns "editor didn't open" rather than
  risking an in-process crash — turning a sharp edge into a clean no-op on the one OS where it can't work.
- **Therefore the window/embed acceptance gate is a standalone main-thread harness, not an xUnit test.** xUnit
  runs test methods on pool threads, so it cannot host AppKit on the main thread. The pure-managed
  `CocoaRunLoop` (timers/posted work, no AppKit) *is* xUnit-testable; the real embed proof is a tiny console
  program whose `Main` (thread 0) opens an `EditorSession` with a fake cocoa editor and asserts the subview
  embedded. See §7.

## 6. Fixtures (`tests/fixtures/mac/`)

Clean-room C sources + a `build-fixtures.sh` (clang), each a minimal **gain** plugin whose **editor is a real
child `NSView`** added under the host content view on parent/attach. Unlike Windows single-DLLs, macOS plugins
are **bundles** (`Contents/MacOS/<binary>` Mach-O + `Contents/Info.plist`); the script lays those out.

| Fixture | Output bundle | Editor | Notes |
|---|---|---|---|
| `clap_gui_fixture.c` | `midisharp_gui.clap` | `clap.gui` cocoa + `clap.timer-support` | id `midisharp.test.gui`; reports 320×240; counts `on_timer` into the first bytes of `clap.state` (proves the run loop drives it). A second id `midisharp.test.gain` with no editor mirrors the Linux/Win pair. |
| `vst2_gui_fixture.c` | `midisharp_gui_vst2.vst` | `effEditOpen`/`effEditGetRect`/`effEditClose` | clean-room `AEffect`; child `NSView` on `effEditOpen(parentView)`. |
| `vst3_gui_fixture.c` | `MidiSharpGui.vst3` (`Contents/MacOS/MidiSharpGui`) | `IPlugView` `"NSView"` | `vst3_c_api.h`-shaped; single-component; `attached(view,"NSView")` adds the child. macOS bundle layout. |

- **Sources are checked in; build outputs are git-ignored.** `build-fixtures.sh` resolves clang (`/usr/bin/clang`,
  confirmed present), compiles each as a `-bundle` linking `-framework AppKit`, and writes the bundle tree to
  `tests/fixtures/mac/out/`. Add `tests/fixtures/mac/out/` to `.gitignore` (the repo's `bin/` rule doesn't cover
  it). The fixtures must be **ad-hoc code-signed** (`codesign -s -`) so Gatekeeper/library-validation lets the
  host load them.
- **Discovery:** a `MacFixtures` test helper returns the output dir (overridable via env
  `MIDISHARP_MAC_FIXTURES`); the cocoa gate scans that dir, so it needs no system-wide plugin install.
- **No third-party plugins are installed on this Mac** (`~/Library/Audio/Plug-Ins/{CLAP,VST3,VST,Components}`
  are empty/absent), so the clean-room fixtures are the only locally-runnable proof; any "real-plugin" test
  self-skips here, exactly as on Linux.

## 7. Tests (`tests/MidiSharp.Hosting.Tests` + one harness)

- **`CocoaRunLoopTests` (xUnit, always-on, pure managed):** timers fire on pump, posted work runs, fd
  registration is a harmless no-op. The macOS sibling of `Win32RunLoopTests`. Self-skips off macOS. This is the
  only AppKit-free piece, so it lives in xUnit.
- **`MidiSharp.Hosting.MacEditorHarness` (new console project, the embed gate):** `Main` runs on thread 0,
  inits `NSApp`, opens an `EditorSession` for a managed `FakeCocoaEditorGui` (which adds a child `NSView` on
  `SetParent`), pumps, and asserts `EmbeddedChildCount ≥ 1`, then resize and close; prints `PASS`/`FAIL` and
  exits 0/1. Run via `dotnet run --project tests/.../MacEditorHarness`. This is the always-available
  backend gate (no native fixture, no toolchain) — the main-thread analogue of `Win32EditorWindowTests`.
- **Per-format cocoa fixture checks:** run against the §6 fixtures through the **out-of-process worker** (which
  pumps on its main thread) or the harness: `IsApiSupported("cocoa")` true / `"x11"` false; embedding produces a
  child `NSView`; the CLAP fixture's timer ticks climb under the host run loop. These self-skip when a fixture
  isn't built.
- **Make `ClapGuiTests`' `IsApiSupported` assertion per-OS** (cocoa on macOS, win32 on Windows, x11 on Linux)
  rather than hardcoded x11.

## 8. Build / toolchain

clang is on PATH (`/usr/bin/clang`, confirmed). The .NET 10 SDK is on `PATH` (`dotnet --version` → `10.0.301`),
so build/test/run with plain **`dotnet`** — verified on this Mac: the **full solution builds 0/0** and the
**existing suite passes (270 passed, 47 skipped, 0 failed)**, the skips being the X11/Win32-only and live-plugin
tests self-skipping where they should. `timeout`/`gtimeout` is absent — don't wrap commands in it. The new
backend files compile into the existing `MidiSharp.Hosting.EditorHost` (net10.0) assembly; the harness is one
new test-side console project. Target build result: 0 warnings, 0 errors; existing Linux/Windows tests unaffected.

## 9. Correctness & threading notes

- `[NSApplication sharedApplication]` + `setActivationPolicy:Regular` (or `Accessory`) set once on the main
  thread; guarded so a host that already created `NSApp` is untouched.
- `setReleasedWhenClosed:NO` so the `NSWindow` object survives a user close for `Dispose` to tear down
  deterministically.
- An **autorelease pool per pump** (`NSAutoreleasePool` alloc/init … drain) so the event/string churn doesn't
  leak; selectors and class handles are cached once (cheap, immortal).
- **Retina / points vs pixels.** Cocoa view geometry is in *points*; plugins report editor sizes in *pixels*.
  On a 2× display these differ by `backingScaleFactor`. The fake-fixture gate controls its own sizes so it's
  unaffected; real-plugin sizing may need a `/ backingScaleFactor` (or set the view's frame in points) — flagged
  as an implementation open item (§12), not a backend blocker.
- If a future delegate is needed (e.g. live resize from the plugin), prefer an `[UnmanagedCallersOnly]` C#
  function pointer rooted for the window lifetime over a managed-delegate marshal.

## 10. Acceptance gate (measured, mirroring the Linux/Win gates)

1. **Run loop (xUnit, always runs):** `CocoaRunLoopTests` — a registered timer fires while pumping, posted work
   runs, `RegisterFd` no-ops without throwing.
2. **Backend embed (harness, always available):** `MacEditorHarness` embeds the managed fake's child `NSView`,
   resizes it, and closes — `EmbeddedChildCount ≥ 1`, child tracks `Resize`, `ShouldClose` latches on close.
3. **CLAP:** the cocoa gui fixture reports an editor, `IsApiSupported("cocoa")` is true, embedding produces a
   child `NSView`, and the editor's `clap.timer-support` tick count climbs under the host run loop.
4. **VST2:** the cocoa gui fixture embeds a child `NSView` via `effEditOpen`; `IsApiSupported("cocoa")` true.
5. **VST3:** the cocoa gui fixture embeds a child `NSView` via `attached(view,"NSView")`;
   `IsApiSupported("cocoa")` true; the separate-controller and IBStream paths are unaffected.
6. Solution builds 0/0 (via `dotnet`); the X11 and Win32 backends and all platform-agnostic code are
   behaviorally unchanged.

## 11. Out of scope

- **AU (Audio Units)** — the genuinely macOS-only *format*, a separate adapter (`AudioComponent`/`AUAudioUnit`
  + AU cocoa views), still parked in `docs/plugin-hosting-plan.md`. Not editor parity; its own future track.
- **CLAP `clap.posix-fd-support` on macOS** — `RegisterFd` is a no-op for now (§2); `CFFileDescriptor`
  integration is a later enhancement if a real plugin needs it.
- **x86_64 macOS** — arm64 only; the `objc_msgSend_stret` path for Intel `NSRect` returns is deferred and the
  backend reports unavailable there.
- **In-process editor hosting on macOS** — not built and not needed: editors are sandboxed-only (§5.3). The
  shared in-process branch (sandbox off) merely fails safe via the main-thread guard; `EditorWindow` is left
  X11/Win32-only.
- **The web mixing console** — OS-agnostic HTML/JS, unchanged.

## 12. Open items to verify during implementation (not blockers)

- ~~Confirm a struct-returning `objc_msgSend` declared to return `CGRect` marshals correctly on arm64~~ —
  **verified 2026-06-22** by an arm64 interop spike (net10, this Mac): `CGRect`-by-value args
  (`initWithContentRect:`/`initWithFrame:`), `CGRect` return-by-value (`frame` → `320×240`), `nuint` return
  (`count`), `NSView` `addSubview:` embedding (`subviews count` `0→1`), and the `NSApp` event pump all marshal
  correctly. No `_stret`/out-param fallback needed on arm64.
- Confirm CLAP truly needs zero changes (the cocoa fixture test is the proof), as on Windows.
- Confirm `[NSScreen screens] count]`-based availability self-skips cleanly under a headless CI/SSH session.
- Pick the final Retina sizing rule (points vs pixels) against a real plugin once one is installed.
- Confirm ad-hoc signing (`codesign -s -`) is sufficient for the host (unsigned, hardened-runtime-off local
  build) to `dlopen` the fixture bundles without `com.apple.security.cs.disable-library-validation`.
