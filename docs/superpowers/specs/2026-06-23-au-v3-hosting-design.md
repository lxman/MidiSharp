# Audio Unit v3 (`AUAudioUnit`) Hosting — Design

**Goal:** host **AU v3** plugins natively on macOS — `AUAudioUnit` instantiation (async, in- or out-of-process),
audio via the `renderBlock`, parameters via the `AUParameterTree`, state via `fullState`, MIDI for instruments
via `scheduleMIDIEventBlock`, and the editor via `requestViewControllerWithCompletionHandler:` — as a second
code path inside the existing `MidiSharp.Hosting.AudioUnit` adapter (the **full native v3 path**, the
user-chosen scope).

**Status:** scoping (no code). Delivered as three coherent slices (§13): **Plan A — v3 core** (block interop +
async instantiation + audio + params + state), **Plan B — instruments**, **Plan C — view-controller editor**,
with an optional **Plan D — clean-room v3 fixture**. Builds on the shipped **AU v2 adapter** (v0.12.0); AU v3
shares that adapter's discovery and `IHostedPlugin`/`IPluginGui` seams and **adds no new project**.

**The two facts that shape everything (probe, 2026-06-23):**
1. **The v2 `AudioComponent` bridge already hosts every v3 AU that also exposes v2** (the majority): a v3 AU
   loads through the shipped `AudioUnitPlugin` via `AudioComponentInstanceNew`, with audio/params/state/CocoaUI
   editor all working. So v3 support is **not** a new adapter — it's the genuinely-v3-only surface.
2. **No v3 AU is installed on this Mac** (Apple ships none hostable; Surge/OldSkoolVerb are v2; `pluginkit`
   shows no AUv3 extensions). **But `AUAudioUnit` wraps v2 components too** (`AUAudioUnitV2Bridge`), so the v3
   code path is verifiable against `AULowpass`/Surge *wrapped as an `AUAudioUnit`* — only async-*required*
   instantiation and out-of-process loading stay unverifiable here.

---

## 1. Where it plugs in

Discovery is **already shared and v3-aware**: `AudioComponentFindNext` (in `AudioUnitFormat.Scan`) enumerates v3
components alongside v2; the `kAudioComponentFlag_IsV3AudioUnit` bit distinguishes them. So no discovery change —
only `AudioUnitFormat.Load` gains a branch: **v3-flagged components route to a new `AudioUnitV3Plugin`**, others
keep the v2 `AudioUnitPlugin`. Both implement the unchanged `IHostedPlugin`/`IPluginGui`. The engine, the worker,
the sandbox, the registry, and the Cocoa editor backend are all untouched.

## 2. Guiding decisions (locked)

- **Full native v3 path** (user-chosen): for a v3 component, use the real `AUAudioUnit` object — async
  `instantiateWithComponentDescription:options:completionHandler:`, the `renderBlock`, the `AUParameterTree`,
  `fullState`, `scheduleMIDIEventBlock`, and `requestViewControllerWithCompletionHandler:` — **not** the v2
  bridge. (The v2 bridge stays the path for non-v3 components.)
- **The Objective-C block ABI is the load-bearing risk** (§5.1, §12). `AUAudioUnit` is block-centric: the host
  must **invoke** blocks the AU vends (`renderBlock`, `scheduleMIDIEventBlock`) and **construct** blocks to hand
  the AU (the render `pullInputBlock`, the instantiation/view-controller completion handlers). C# has no native
  block support, so we build the block struct by hand (a `_NSConcreteGlobalBlock`/stack-block layout with an
  `invoke` pointing at an `[UnmanagedCallersOnly]` function, and a captured context pointer for per-instance
  state). A **Task 0 spike proves this before any adapter code** — the v3 analogue of the v2 render-shim spike.
- **AudioToolbox + AVFoundation + AppKit via direct P/Invoke / `objc_msgSend`**, reusing the adapter's
  `AuAppKit`/`CoreFoundation` slices. `AUAudioUnit`/`AUParameter*`/`AVAudioFormat` are Obj-C classes messaged via
  `objc_msgSend`; no binding package. Audio stays **non-interleaved float32**, mapping straight onto the planar
  bus (an `AVAudioFormat` standard format is non-interleaved float).
- **Route by the v3 flag; default in-process.** `Load` picks `AudioUnitV3Plugin` when
  `componentFlags & kAudioComponentFlag_IsV3AudioUnit`. Instantiation defaults to **in-process**
  (`kAudioComponentInstantiation_LoadInProcess`) — our own sandbox worker already provides crash isolation, so
  the native **out-of-process** option (`LoadOutOfProcess`) is **supported but opt-in** (§11): it routes the
  render block through Apple's XPC, whose latency/threading doesn't fit our lock-step model well, and it
  duplicates isolation we already have.
- **Verification against v2-wrapped-as-`AUAudioUnit`** (§6). The `AUAudioUnit` machinery is exercised end-to-end
  against `AULowpass`/Surge wrapped by `AUAudioUnitV2Bridge`; genuinely-v3-only behaviors (async-required load,
  OOP) self-skip until a real v3 AU is installed. Honest: the code is *proven for the API surface* but
  *unproven against a true v3-only plugin* on this machine.
- **arm64 only**, matching the rest of the adapter.

## 3. Invariants (regression guards)

1. **The shipped v2 path is untouched.** `AudioUnitPlugin`, `AudioUnitAbi`, discovery, the editor neutral-background
   fix — all unchanged. Non-v3 components still load exactly as in v0.12.0.
2. **`renderBlock` invocation is realtime-clean:** no managed allocation, no locks on the audio path. The
   constructed `pullInputBlock` is an `[UnmanagedCallersOnly]` function; its captured context is a pinned
   `GCHandle`. All block structs are built once in `Activate`, reused per block.
3. **Additive and macOS-only.** No new project; the v3 path compiles into the existing adapter and is reached
   only for v3 components on macOS. Linux/Windows builds and the other adapters are unchanged.
4. **Dependency-free.** Only AudioToolbox/AVFoundation/AppKit/CoreFoundation system P/Invoke.

## 4. Architecture — what does NOT change

`AudioUnitFormat` discovery (registry + Info.plist), the `IHostedPlugin`/`IPluginGui` contracts, `PlanarBuffers`,
`HostEvent`, the worker's name→format switch (already maps `"AU"`), the sandbox, the Cocoa `EditorHost` backend,
and the v2 `AudioUnitPlugin` are all unchanged. AU v3 is a sibling plugin class the format selects per-component.

## 5. Components (added to `MidiSharp.Hosting.AudioUnit`)

### 5.1 `AuBlocks.cs` — the Objective-C block bridge (the load-bearing slice)

- **Invoke a vended block:** a block is a pointer to `{ void* isa; int flags; int reserved; void* invoke; void*
  descriptor; …captures }`. The `invoke` function pointer sits at **offset 16** (64-bit). `InvokeRender(block,
  …)` reads `*(void**)(block+16)` and calls it as `delegate* unmanaged[Cdecl]<block, args…, ret>` with the
  block pointer as the first argument. Used for `renderBlock` and `scheduleMIDIEventBlock`.
- **Construct a block to hand the AU:** allocate `{ isa = &_NSConcreteGlobalBlock; flags; reserved; invoke =
  &OurUnmanagedFn; descriptor = &Desc; void* context }` where `Desc = { ulong reserved=0; ulong size }`. The
  captured `context` (offset 32) carries a `GCHandle` so the `[UnmanagedCallersOnly]` invoke can recover the
  owning plugin. Used for `pullInputBlock` and the completion handlers. **(Offsets/`_NSConcreteGlobalBlock`
  symbol confirmed by the Task 0 spike before anything depends on them.)**
- Loads `/System/Library/Frameworks/AVFoundation.framework/AVFoundation` so `AVAudioFormat`/`AUAudioUnit`
  classes resolve; `libobjc`/AppKit come from the existing `AuAppKit`.

### 5.2 `AudioUnitV3Plugin : IHostedPlugin, IPluginGui`

- **Activate (async instantiation, awaited):** `[AUAudioUnit instantiateWithComponentDescription:options:
  completionHandler:]` with a constructed completion block; the host blocks (a managed reset event) until the
  handler fires with the `AUAudioUnit` (or `NSError`). Then: set the output (and, for effects, input) bus
  `format` to a stereo non-interleaved-float `AVAudioFormat`; set `maximumFramesToRender`; build the
  `pullInputBlock` (effects); read the cached `renderBlock`; `allocateRenderResourcesAndReturnError:`.
- **Process (realtime):** stash the block's input channels; build a stereo `AudioBufferList` over the output
  channels; `InvokeRender(renderBlock, &flags, &ts, frames, /*outputBus*/0, &abl, /*events*/null,
  pullInputBlock)`. The `pullInputBlock` serves the stashed input. Mirror mono→stereo. (Same shape as the v2
  render shim, but the call target is a block, not a registered C callback.)
- **Parameters (§5.3), State (§5.4), Editor (§5.6), MIDI (§5.5, Plan B).**
- **Dispose:** `deallocateRenderResources`, release the `AUAudioUnit` and constructed blocks, free the
  `GCHandle`s.

### 5.3 Parameters — the `AUParameterTree`

`AUAudioUnit.parameterTree.allParameters` → `AUParameter[]`; each exposes `address` (`AUParameterAddress`,
`uint64`), `value` (`AUValue`, `float`), `minValue`/`maxValue`/`displayName`. Build one `PluginParameter` each
(min/max → normalize) keyed by `address`. `GetParameter` reads `param.value`; `SetParameter` writes
`param.value = denorm` (off the audio thread). Sample-accurate automation uses the AU's `scheduleParameterBlock`
(a vended block) — a later refinement; block-less `value` sets cover the host's needs first.

### 5.4 State — `fullState`

`fullState` is an `NSDictionary` property. `SaveState` serializes it to a binary plist (the existing
`CoreFoundation.ToData` over the dictionary, which is a CF type); `LoadState` parses bytes →
`CFPropertyListCreateWithData` → set `fullState`. (`fullStateForDocument` is the larger variant; `fullState`
suffices.)

### 5.5 Instruments (Plan B)

A music-device v3 AU has no input bus → no `pullInputBlock`; `IsInstrument` from the component type. MIDI
`HostEvent`s go through the vended `scheduleMIDIEventBlock(eventSampleTime, cable, length, midiBytes)` ahead of
`InvokeRender`. (Newer AUs prefer `scheduleMIDIEventListBlock`; the 3-byte block covers our `HostEvent`s.)

### 5.6 Editor (Plan C) — `requestViewControllerWithCompletionHandler:`

`[au requestViewControllerWithCompletionHandler:]` takes a constructed completion block; on the **main thread**
it fires with an `NSViewController` whose `.view` is the editor `NSView`. `IPluginGui.Create` requests it (awaits
the handler), `SetParent` adds that view under the host content view (the existing Cocoa backend + neutral
background), `TryGetSize` from the view's frame. AUs that vend no view controller fall back to the v2 path's
`AUGenericView`. Rides the unchanged `EditorSession`.

## 6. Verification — v2-wrapped-as-`AUAudioUnit` (no v3 plugin needed for the machinery)

| Surface | Verified against | How |
|---|---|---|
| Async instantiation, renderBlock, params, state | `AULowpass` / Surge **wrapped by `AUAudioUnitV2Bridge`** | instantiate as `AUAudioUnit`, render a block (filter acts), sweep a param, round-trip `fullState` |
| Instruments (Plan B) | Apple `DLSMusicDevice` wrapped as `AUAudioUnit` | a note via `scheduleMIDIEventBlock` renders non-silence |
| Editor (Plan C) | `AULowpass`/Surge view controller via `requestViewController…` | child `NSView` embeds (`EmbeddedChildCount ≥ 1`) on the main-thread harness |
| **v3-only:** async-*required* load, `LoadOutOfProcess` | a real v3 AU | **self-skips** — none installed; documented as unproven here |

This is the honest split: the *entire `AUAudioUnit` API path* is provable against v2-wrapped units; only the
truly-v3-only instantiation edges await a real v3 plugin (or Plan D's fixture).

## 7. Tests

- **xUnit, macOS-only, self-skipping off-OS** (`AudioUnitV3Tests`): instantiate `AULowpass` as `AUAudioUnit`,
  render (filter acts), param sweep, `fullState` round-trip; `DLSMusicDevice` note (Plan B). The render path is
  not AppKit, so it stays in xUnit.
- **Main-thread `MacEditorHarness`** (Plan C): an `EmbedAuV3` arm requests the view controller for `AULowpass`
  (as `AUAudioUnit`) and asserts the child `NSView` embeds.
- **Self-skip arms** for "an installed AU that requires async instantiation" and OOP loading — inert until a v3
  AU is present.

## 8. Build / toolchain

`dotnet` (net10.0, arm64); the v3 path compiles into the existing `MidiSharp.Hosting.AudioUnit`. New files:
`AuBlocks.cs`, `AudioUnitV3Plugin.cs`. AVFoundation added to the loaded frameworks. Target **0 warnings / 0
errors**; v2 path and other suites unchanged.

## 9. Correctness & realtime notes

- **Block lifetime.** Constructed blocks (pullInput, completion) and their captured `GCHandle`s outlive every
  call that can run them; freed only in `Dispose` after `deallocateRenderResources`. Completion blocks are
  one-shot but kept rooted until fired.
- **`renderBlock` RT-safety.** Invoking a block is a plain indirect call — no allocation. The `pullInputBlock`
  copies stashed input; no managed alloc/lock. Same lock-step single-thread model as v2 (no separate audio
  thread), so no `start/stop`-style thread-role trap.
- **Main-thread async.** Instantiation and `requestViewController` completion handlers are dispatched on a
  run-loop; the worker/harness pump their main thread, so the awaited handler fires. The audio path never waits
  on the main thread.
- **AVAudioFormat.** Build a stereo non-interleaved float `AVAudioFormat` once; a format mismatch fails
  `allocateRenderResources`, so set bus formats first (the v3 analogue of v2's ASBD-before-initialize order).

## 10. Acceptance gate (per slice)

**Plan A:** `AULowpass` instantiates as an `AUAudioUnit`, renders (output differs from input — filter acts), a
param sweep changes output, `fullState` round-trips; v2 path and full suite unchanged; build 0/0. **Plan B:**
`DLSMusicDevice` (as `AUAudioUnit`) renders non-silence from a `scheduleMIDIEventBlock` note. **Plan C:** an AU's
v3 view controller embeds a child `NSView` through the unchanged `EditorSession` (`PASS AU-v3` in the harness).

## 11. Out of scope / opt-in

- **Out-of-process (`LoadOutOfProcess`) loading** — supported as an opt-in flag but **off by default**: our
  sandbox worker already isolates crashes, and routing `renderBlock` through Apple's XPC doesn't fit the
  lock-step model. Documented, not default.
- **`scheduleParameterBlock` sample-accurate automation** and `scheduleMIDIEventListBlock` (MIDI 2.0) — later
  refinements; `param.value` sets and 3-byte MIDI cover the host now.
- **AUv3 user presets / `AUAudioUnitPreset`** beyond `fullState` — later.
- **A real v3-only plugin acceptance** — gated on one being installed (or Plan D).

## 12. Open items / risks (Task 0 spike de-risks first)

- **Prove the block ABI end-to-end** (the load-bearing spike): construct a completion block and async-instantiate
  `AULowpass` as an `AUAudioUnit`; construct a `pullInputBlock`; read and **invoke** the `renderBlock` with it;
  confirm filtered output. Confirm the block layout (invoke @16, context @32), `_NSConcreteGlobalBlock`/
  `_NSConcreteStackBlock` symbol, and `AudioComponentInstantiationOptions` values against the runtime. This one
  spike validates instantiation + render + block construction before Plan A's real code.
- Confirm `AUAudioUnitV2Bridge` exposes `parameterTree`, `fullState`, and a view controller for a v2 unit (the
  verification strategy depends on it).
- Confirm `requestViewControllerWithCompletionHandler:` returns a usable `NSViewController.view` on the main
  thread for a v2-wrapped unit.

## 13. Plan breakdown (coherent slices)

- **Plan A — v3 core** (`…au-v3-core.md`): `AuBlocks` + the **Task 0 block spike**, async instantiation, the
  render-block audio path, parameters, state. The risky, load-bearing slice.
- **Plan B — v3 instruments** (`…au-v3-instruments.md`): `scheduleMIDIEventBlock`, no input bus. Small after A.
- **Plan C — v3 editor** (`…au-v3-editor.md`): `requestViewControllerWithCompletionHandler:` riding the existing
  Cocoa backend; generic fallback.
- **Plan D — clean-room v3 fixture** (optional, `…au-v3-fixture.md`): a minimal AUv3 app-extension to give true
  v3-only verification. Heavy (app bundle + Info.plist extension point + signing + registration); only if real
  v3-only proof is wanted before a third-party v3 AU is available.
