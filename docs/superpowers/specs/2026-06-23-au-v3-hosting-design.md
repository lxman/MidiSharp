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

**The facts that shape everything (probe, 2026-06-23):**
1. **The v2 `AudioComponent` bridge already hosts every v3 AU that also exposes v2:** such a v3 AU loads through
   the shipped `AudioUnitPlugin` via `AudioComponentInstanceNew`. So v3 support is **not** a new adapter — it's
   the genuinely-v3-only surface.
2. **Five real v3 AUs are installed**, covering both load profiles: four **effects** (`DimChorus`, `FakeTake`,
   `LevLr`, `J_NO Chorus`) that are `RequiresAsync` + `CanLoadInProcess` = **false** → **out-of-process-only**;
   and one **instrument**, **`AudMod`** (Unlikelyware, `aumu:audM:nLkL`), that is `RequiresAsync` +
   `CanLoadInProcess` = **true** → loadable **in-process**. **All are `RequiresAsync`**, so none load through
   the synchronous v2 bridge — confirming async + flag-honored loading is the *required* path (correcting an
   earlier assumption that OOP/async was optional). The design's load-flag routing is now exercised by **both**
   profiles.
3. **`AUAudioUnit` also wraps v2 components** (`AUAudioUnitV2Bridge`), an extra in-process synchronous sanity
   target (`AULowpass`). **Every slice now has a real v3 target** — effects (Plan A), instrument (Plan B,
   `AudMod`), editor (Plan C) — so the clean-room fixture (Plan D) is **unnecessary**.

---

> ## ⚠ Spike outcome (2026-06-23) — design simplified; supersedes §2/§5 below
>
> The Plan A Task 0 spike proved a far simpler reality, so the `AUAudioUnit`-object design drafted in §2/§5 is
> **not** what gets built:
> - **`AudioComponentInstantiate` (async, one global completion block) + the existing v2 C API drives v3 AUs**,
>   in- and out-of-process. `AudioUnitRender`, the input render callback, the parameter C-API, `ClassInfo` state,
>   and `MusicDeviceMIDIEvent` all work over Apple's bridge. So **audio/params/state/MIDI need no new code** —
>   the shipped `AudioUnitPlugin` is reused verbatim; `Load` just gains an **async-load branch**.
> - **OOP render latency is ~18 µs / 10.67 ms block (0.2%)** — the XPC concern is moot.
> - **`CocoaUI` is absent for v3 AUs**, so the **editor** (`requestViewControllerWithCompletionHandler:`, the one
>   piece needing the `AUAudioUnit` Obj-C object) is the only genuinely-v3 work.
>
> Net: **no `AudioUnitV3Plugin`, no `renderBlock`/`pullInputBlock`/`AUParameterTree`/`fullState` Obj-C, no
> `scheduleMIDIEventBlock`.** The block ABI shrinks to one completion block. **Plan A** = async-load branch (+
> the completion block); v3 effects **and** instruments come for free over the bridge. **Plan B** = the v3
> editor. The current authoritative tasks live in the **plan files**; §2/§5 are kept for the record only. See
> `plans/2026-06-23-au-v3-core.md` Task 0 for the measured spike results.

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
- **Route by the v3 flag; honor the component's load flags.** `Load` picks `AudioUnitV3Plugin` when
  `componentFlags & kAudioComponentFlag_IsV3AudioUnit`. Instantiation respects the flags: **out-of-process**
  (`kAudioComponentInstantiation_LoadOutOfProcess`) when `CanLoadInProcess` is clear (which is the case for all
  four installed v3 AUs), in-process otherwise; **always async** (`instantiateWithComponentDescription:options:
  completionHandler:`) since the real units set `RequiresAsync`. The render block proxies to Apple's AU
  extension host over XPC; our lock-step `Process` simply **invokes the block** (it blocks until the cross-process
  render returns — acceptable for the offline/lock-step model; we measure the added latency in the spike). Our
  sandbox worker still wraps all of this, so a v3 AU runs in Apple's extension process *and* under our worker —
  doubled isolation, harmless.
- **Verification against real v3 AUs + a v2-wrapped check** (§6). Plan A/C exercise the genuine async + OOP path
  against the installed v3 effects (`DimChorus`/`FakeTake`/`LevLr`/`J_NO Chorus`); a v2-wrapped
  `AULowpass`-as-`AUAudioUnit` adds an in-process synchronous check of the `AUAudioUnit` API basics. No v3
  instrument is installed, so Plan B verifies against a v2-wrapped `DLSMusicDevice` (self-skipping the real-v3
  instrument arm).
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

## 6. Verification — real v3 AUs (async + OOP) plus a v2-wrapped API check

| Surface | Verified against | How |
|---|---|---|
| **Async + OOP instantiation, renderBlock, params, state** | the real v3 effects (`DimChorus` etc.) | async-instantiate `LoadOutOfProcess`, render a block (the chorus audibly alters the signal), sweep a param, round-trip `fullState` |
| `AUAudioUnit` API basics (in-process, sync) | `AULowpass` **wrapped by `AUAudioUnitV2Bridge`** | instantiate as `AUAudioUnit`, render (filter acts) — a fast non-XPC sanity path |
| Instruments (Plan B) | the real v3 instrument **`AudMod`** (in-process) | a note via `scheduleMIDIEventBlock` renders non-silence |
| Editor (Plan C) | a real v3 AU's view controller (`DimChorus`/`AudMod`) via `requestViewController…` | child `NSView` embeds (`EmbeddedChildCount ≥ 1`) on the main-thread harness |

**Every slice gets real v3 coverage** — and the two load profiles (`AudMod` in-process, the effects
out-of-process) exercise both instantiation paths. The v2-wrapped `AULowpass` remains a fast in-process sanity
check.

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

- **`scheduleParameterBlock` sample-accurate automation** and `scheduleMIDIEventListBlock` (MIDI 2.0) — later
  refinements; `param.value` sets and 3-byte MIDI cover the host now.
- **AUv3 user presets / `AUAudioUnitPreset`** beyond `fullState` — later.
- **A real v3-only plugin acceptance** — gated on one being installed (or Plan D).

## 12. Open items / risks (Task 0 spike de-risks first)

- **Prove the block ABI + async/OOP path end-to-end** (the load-bearing spike): construct a completion block and
  async-instantiate **`DimChorus`** (a real v3 effect, `LoadOutOfProcess`); construct a `pullInputBlock`; read
  and **invoke** the `renderBlock` with it; confirm the chorus alters the signal. Also run the faster in-process
  **v2-wrapped `AULowpass`** variant. Confirm the block layout (invoke @16, context @32), the
  `_NSConcreteGlobalBlock`/`_NSConcreteStackBlock` symbol, and the `AudioComponentInstantiationOptions` values
  against the runtime, and **measure the OOP `renderBlock` latency** (does an XPC-proxied render fit the
  lock-step block budget?).
- Confirm `parameterTree` + `fullState` on a real v3 AU (`DimChorus`) and on a v2-wrapped unit.
- Confirm `requestViewControllerWithCompletionHandler:` returns a usable `NSViewController.view` on the main
  thread for a real v3 AU.

## 13. Plan breakdown (post-spike — two slices)

- **Plan A — v3 async instantiation** (`…au-v3-core.md`): the async-load branch in `AudioUnitFormat.Load` (one
  completion block + `AudioComponentInstantiate`), routing v3 components through the **existing**
  `AudioUnitPlugin`. v3 **effects and instruments** both come over the bridge — verified against `DimChorus`
  (OOP) and `AudMod` (in-process). Task 0 spike **done**.
- **Plan B — v3 editor** (`…au-v3-editor.md`): `requestViewControllerWithCompletionHandler:` via the `AUAudioUnit`
  object (since `CocoaUI` is absent for v3), riding the existing Cocoa backend; `AUGenericView` fallback. The
  only genuinely-v3-specific implementation.
- ~~Plan C — instruments~~: **folded into Plan A** — v3 instruments need nothing beyond the async-load branch
  (`MusicDeviceMIDIEvent` already works over the bridge). ~~Plan D fixture~~: dropped (real v3 targets installed).
- ~~**Plan D — clean-room v3 fixture**~~ — **not needed.** Real v3 targets now cover every slice (effects for
  Plan A, the `AudMod` instrument for Plan B, view controllers for Plan C), and both load profiles
  (in-process + out-of-process). Dropped.
