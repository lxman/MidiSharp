# Audio Unit (AU) Hosting — Design

**Goal:** host **Audio Unit (AU v2) effects and instruments** on macOS as a new format adapter
`MidiSharp.Hosting.AudioUnit`, at functional parity with the CLAP/VST2/VST3 adapters — discovered through the
system component registry, run through the engine's planar/event plumbing, persisted into setups, and (Plan C)
opening their native Cocoa editor through the existing `EditorHost`. AU is the one genuinely **macOS-only**
format and the last major format left parked in [`docs/plugin-hosting-plan.md`](../../plugin-hosting-plan.md).

**Status:** scoping (no code yet). This spec covers the whole AU effort; it is delivered as **three coherent,
independently-shippable slices** (§13): **Plan A — effects core**, **Plan B — instruments**, **Plan C — Cocoa
editor**. **AU v3 (`AUAudioUnit`) is out of scope** for this effort (§11) — v2 (`AudioComponent` C API) is the
first target: widest installed-plugin coverage, smallest interop surface, and a structural parallel of the
existing C-ABI adapters.

---

## 1. Where it plugs in

The format seam already exists and three adapters travel it:

- `IPluginFormat` (`MidiSharp.Hosting/IPluginFormat.cs`) — discover (`EnumerateFiles` → `ScanFile`, the
  crash-resilient split) and `Load`. One implementation owns all of a format's native interop.
- `IHostedPlugin` (`IHostedPlugin.cs`) — the lifecycle the engine drives: `Activate` → realtime `Process` →
  `Deactivate` → `Dispose`, plus `GetParameter`/`SetParameter`, `SaveState`/`LoadState`, and `Gui`.
- `PlanarBuffers` / `HostEvent` — non-interleaved float channels and sample-accurate MIDI/param events. AU's
  native audio format is **non-interleaved float**, so (like CLAP) the adapter works directly in planar and the
  interleaved `PlanarBridge` is not involved.
- `PluginRegistry` aggregates `IPluginFormat`s; the worker/server discover and load through it. Adding AU =
  one more `IPluginFormat` registered on macOS.
- `IPluginGui` / `EditorHost` (Plan C) — the format-agnostic editor lifecycle and the per-OS window backend.
  The **Cocoa backend already exists** (`MidiSharp.Hosting.EditorHost/MacArm/`), so the AU editor reuses it and
  only adds AU-side view vending.

Adding AU means a new adapter project `MidiSharp.Hosting.AudioUnit` (net10.0, macOS-only) with: an
`AudioToolbox`/`CoreFoundation` interop slice, a format (registry-based discovery), a plugin
(`IHostedPlugin`), the **pull→push render shim** (§5.3), and — in Plan C — an `IPluginGui` over
`kAudioUnitProperty_CocoaUI`.

## 2. Guiding decisions (locked)

- **AU v2 (`AudioComponent`/`AudioUnit`) first.** The C API: `AudioComponentFindNext`,
  `AudioComponentInstanceNew`, `AudioUnitSetProperty`/`AudioUnitRender`/`AudioUnitGetParameter`. Mirrors the
  existing clean C-ABI adapters (no C++ vtable bridging like VST3; no Obj-C object model like v3). v3 deferred
  (§11).
- **AudioToolbox + CoreFoundation via direct P/Invoke**, dependency-free, mirroring every other adapter. Load
  `/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox` (AudioComponent/AudioUnit/MusicDevice),
  `…/CoreFoundation.framework/CoreFoundation` (the `kAudioUnitProperty_ClassInfo` plist round-trip, CFURL/CFString
  for the editor), and `…/CoreAudioKit.framework/CoreAudioKit` (Plan C generic-view fallback). **No
  AVAudioEngine / AudioToolbox binding package.**
- **Pull→push render shim is the load-bearing design choice (§5.3).** AU is *pull*: the host calls
  `AudioUnitRender` and the AU pulls its input from an `AURenderCallback` the host registers. Our engine is
  *push* (`Process(input, output, events)`). The adapter bridges by stashing the block's input-channel pointers,
  pointing the output `AudioBufferList` at our output channels, and supplying input from an
  `[UnmanagedCallersOnly]` render callback. This is the one place AU diverges structurally from the push formats
  and the main correctness risk; it is de-risked by a spike first (Plan A Task 0).
- **Non-interleaved float32, planar in/out** (`kAudioFormatFlagsNativeFloatPacked | …NonInterleaved`), so the AU
  reads/writes our `PlanarBuffers` channel pointers directly — no interleave/copy on the hot path, same as CLAP.
- **Discovery is registry + Info.plist, not dlopen.** Unlike CLAP/VST (`dlopen` a file), AU components are
  registered with the OS; you instantiate via `AudioComponentFindNext(desc)` → `AudioComponentInstanceNew`. The
  crash-safe `ScanFile` reads a `.component` bundle's **`Info.plist` `AudioComponents` array**
  (type/subtype/manufacturer/name) — **no native code loaded** — exactly the resilience `EnumerateFiles`/
  `ScanFile` was designed for. `Load` then resolves the live `AudioComponent` from that 3-tuple.
- **Verification uses Apple's always-present system AUs — essentially no fixtures.** Unlike VST3/CLAP (which
  needed a real third-party plugin, Surge XT, because nothing else was installed), macOS **always** ships system
  Audio Units: `AULowpass`/`AUDelay`/`AUMatrixReverb` (effects, manufacturer `'appl'`) and `DLSMusicDevice` (a
  built-in synth, instrument). Plan A/B verify against those with **zero install and no clean-room fixtures**.
  Only Plan C's *custom Cocoa view* needs a real third-party AU (Surge XT ships an `.component`, already
  installed) — Apple built-ins use the generic view.
- **arm64 only**, consistent with the Cocoa editor backend and the dev/CI target. (AU itself is
  architecture-neutral C; this just matches the rest of the macOS surface and avoids an x86_64 toolchain claim
  we don't test.)
- **Lock-step worker, no separate audio thread** — same model as the other adapters. `AudioUnitRender` runs on
  the command thread inside `Process`; AU offline render does not require a dedicated RT thread, and AU has no
  Surge-style thread-check to satisfy, so the CLAP thread-role subtlety does **not** recur here.

## 3. Invariants (regression guards)

1. **The engine, the synth, and the other adapters are untouched.** AU is purely additive — a new project and
   one `PluginRegistry` registration guarded by `OperatingSystem.IsMacOS()`. CLAP/VST2/VST3/LADSPA behavior is
   unchanged.
2. **`Process` is realtime-clean:** no managed allocation, no locks. The render callback is
   `[UnmanagedCallersOnly]`; all CF/plist work (state) and parameter-list building happen off the audio thread
   in `Activate`/`SaveState`/`LoadState`.
3. **Non-macOS builds are unaffected.** The adapter compiles only meaningfully on macOS; the registry skips it
   elsewhere. The solution still builds 0/0 on Linux/Windows and their suites are behaviorally unchanged.
4. **Dependency-free.** Only `AudioToolbox`/`CoreFoundation`/`CoreAudioKit` system-framework P/Invoke — no new
   package references.

## 4. Architecture — what does NOT change

The `IHostedPlugin`/`IPluginFormat` contracts, `PlanarBuffers`, `HostEvent`, `HostedEffect`/`HostedInstrument`
(the mixer-side wrappers), the out-of-process `Worker`/`Sandbox` proxy, the server endpoints, and the web UI are
all format-agnostic and unchanged. AU appears to all of them as just another `PluginDescriptor.Format == "AU"`.
Plan C's editor rides the **existing** `EditorSession`/Cocoa backend with no change to either — only a new
`IPluginGui` implementation on the AU side.

## 5. Components (new project `MidiSharp.Hosting.AudioUnit`, net10.0)

### 5.1 `AudioUnitAbi.cs` — the AudioToolbox/CoreFoundation interop slice (pure interop, no logic)

- **Structs:** `AudioComponentDescription { uint Type, SubType, Manufacturer, Flags, FlagsMask }`;
  `AudioStreamBasicDescription` (ASBD — `double SampleRate; uint FormatID, FormatFlags, BytesPerPacket,
  FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved`); `AudioBuffer { uint
  NumberChannels; uint DataByteSize; void* Data }` + `AudioBufferList { uint NumberBuffers; AudioBuffer
  Buffers[1] }` (allocated with a 2-buffer tail for stereo non-interleaved); `AudioTimeStamp` (we set
  `mSampleTime` + `kAudioTimeStampSampleTimeValid`); `AURenderCallbackStruct { delegate*… InputProc; void*
  RefCon }`; `AudioUnitParameterInfo`; `AudioUnitCocoaViewInfo` (Plan C).
- **Functions** (P/Invoke into AudioToolbox): `AudioComponentFindNext`, `AudioComponentCopyName`,
  `AudioComponentGetDescription`, `AudioComponentInstanceNew`, `AudioComponentInstanceDispose`,
  `AudioUnitInitialize`/`AudioUnitUninitialize`, `AudioUnitSetProperty`/`AudioUnitGetProperty`/
  `AudioUnitGetPropertyInfo`, `AudioUnitRender`, `AudioUnitGetParameter`/`AudioUnitSetParameter`,
  `AudioUnitReset`, and (Plan B) `MusicDeviceMIDIEvent`. CoreFoundation: `CFPropertyListCreateData`/
  `CFPropertyListCreateWithData`/`CFRelease`/`CFDataGetBytePtr`/`CFDataGetLength`/`CFDataCreate` (state), plus
  CFURL/CFString/CFBundle (Plan C editor).
- **Constants** (OSType four-char codes + property/scope ids). Types: `'aufx'` effect, `'aumf'` music-effect,
  `'aumu'` music-device (instrument), `'augn'` generator; manufacturer `'appl'`. Properties: `ClassInfo=0`,
  `ParameterList=3`, `ParameterInfo=4`, `StreamFormat=8`, `ElementCount=11`, `MaximumFramesPerSlice=14`,
  `SetRenderCallback=23`, `CocoaUI=31`. Scopes: `Global=0`, `Input=1`, `Output=2`. Format flags for native
  packed non-interleaved float32. **(These values are transcribed from `AudioUnit/AudioUnitProperties.h` /
  `CoreAudioTypes.h`; the spike confirms each before the adapter relies on it — see §12.)**

### 5.2 `AudioUnitFormat : IPluginFormat`

- `Name => "AU"`. `DefaultSearchPaths` → the standard component dirs
  (`~/Library/Audio/Plug-Ins/Components`, `/Library/Audio/Plug-Ins/Components`,
  `/System/Library/Components`).
- `EnumerateFiles` → `*.component` **bundles** under those dirs (filesystem only, crash-safe), in sorted order.
- `ScanFile(bundle)` → read `Contents/Info.plist`'s `AudioComponents` array (via CF plist, **no native plugin
  code**) → a `PluginDescriptor` per entry carrying the `type/subtype/manufacturer` 3-tuple (stashed in the
  descriptor's opaque id) and `IsInstrument = type ∈ {'aumu','augn'}`.
- **Plus a registry path for system AUs:** Apple's built-ins are not third-party `.component` files in the user
  dirs, so `Scan` also walks `AudioComponentFindNext` to surface them (this is how `AULowpass`/`DLSMusicDevice`
  appear without a bundle on disk). The two sources are de-duplicated by 3-tuple.
- `Load(descriptor, config)` → `AudioComponentFindNext` for the descriptor's 3-tuple →
  `AudioComponentInstanceNew` → `new AudioUnitPlugin(instance, descriptor, config)`.

### 5.3 `AudioUnitPlugin : IHostedPlugin` — incl. the pull→push render shim

- **`Activate(config)`** (off audio thread): set the **output** scope-Output ASBD (stereo non-interleaved
  float32 at `config.SampleRate`); for **effects**, set the **input** scope-Input ASBD and register the
  `AURenderCallbackStruct` (input proc + `RefCon = GCHandle/this*`); set
  `kAudioUnitProperty_MaximumFramesPerSlice`; build the parameter table (§5.4); `AudioUnitInitialize`. Allocate
  the reusable stereo `AudioBufferList` (2 buffers) and an `AudioTimeStamp` once.
- **`Process(input, output, events)`** (realtime): apply queued/sample-offset param events via
  `AudioUnitSetParameter(…, bufferOffsetInFrames)`; stash `input.Channel(0/1)` pointers + frame count in the
  shim state; point the output `AudioBufferList.mBuffers[c].mData` at `output.Channel(c)`; bump
  `timeStamp.mSampleTime`; call `AudioUnitRender(au, output-bus, frames, &abl)`. The
  **`[UnmanagedCallersOnly]` input render callback** fills the AU's requested `ioData` from the stashed input
  pointers (effects); for an instrument there is no input bus and the callback is never registered. Mono output
  AUs are mirrored to the stereo bus, as CLAP does.
- **Parameters** (§5.4): `GetParameter`/`SetParameter` over `AudioUnitGetParameter`/`AudioUnitSetParameter`
  (scope Global), normalized through `PluginParameter`.
- **State:** `SaveState` → `AudioUnitGetProperty(kAudioUnitProperty_ClassInfo)` yields a `CFPropertyListRef`;
  `CFPropertyListCreateData(binary)` → bytes. `LoadState` → bytes → `CFPropertyListCreateWithData` →
  `AudioUnitSetProperty(ClassInfo)`. Empty when the property is absent.
- **`Deactivate`/`Dispose`:** `AudioUnitUninitialize` + `AudioComponentInstanceDispose`; free the buffer list,
  release any retained CF objects and the callback `GCHandle`.
- **`Gui`** is null until Plan C.

### 5.4 Parameters

`kAudioUnitProperty_ParameterList` (scope Global) → array of `AudioUnitParameterID`; per id,
`kAudioUnitProperty_ParameterInfo` → `AudioUnitParameterInfo` (name, `min/max/defaultValue`, flags). Build one
`PluginParameter` each (min/max/default → normalize/denormalize); keep the parallel `AudioUnitParameterID[]`.
`SetParameter` is realtime-safe (`AudioUnitSetParameter` is designed for the render thread), so sample-accurate
param `HostEvent`s pass their `SampleOffset` as `bufferOffsetInFrames`.

### 5.5 Instruments (Plan B)

A music-device AU (`'aumu'`/`'augn'`) has no audio input bus → no input callback; `IsInstrument = true`.
MIDI `HostEvent`s become `MusicDeviceMIDIEvent(au, status, data1, data2, sampleOffset)` ahead of
`AudioUnitRender`. Verified by rendering a note through Apple's **`DLSMusicDevice`** and asserting non-silence.

### 5.6 Editor (Plan C) — `IPluginGui` over `kAudioUnitProperty_CocoaUI`

`AudioUnitGetProperty(kAudioUnitProperty_CocoaUI)` → `AudioUnitCocoaViewInfo { CFURLRef bundleLocation;
CFStringRef viewClassName[] }`. Load the bundle (`CFBundleCreate` + `CFBundleGetDataPointerForName`/Obj-C class
lookup), instantiate the view-factory class (it conforms to **`AUCocoaUIBase`**:
`-(NSView*)uiViewForAudioUnit:withSize:`), call it to obtain an `NSView`, and in `IPluginGui.SetParent` add that
view under the Cocoa backend's content `NSView`. `TryGetSize` from the returned view's frame. When an AU
provides **no** custom Cocoa UI (Apple built-ins), fall back to CoreAudioKit's generic `AUGenericView`. Drives
through the **existing** `EditorSession` + Cocoa `INativeEditorWindow` unchanged — `EmbeddedChildCount ≥ 1` is
the gate, exactly as for CLAP/VST3.

## 6. Verification "fixtures" = Apple system AUs (+ Surge AU for the editor)

No clean-room fixtures and (mostly) no third-party install:

| Slice | Verified against | Always present? | Asserts |
|---|---|---|---|
| A (effects) | `AULowpass` (`'lpas'/'appl'`), `AUDelay`, `AUMatrixReverb` | Yes (system) | discover, load, set stream format, render a block, audio changes, a parameter sweeps, state round-trips |
| B (instruments) | `DLSMusicDevice` (built-in synth) | Yes (system) | `IsInstrument`, a note via `MusicDeviceMIDIEvent` renders non-silence |
| C (editor) | Surge XT `.component` (custom Cocoa view); Apple built-in via generic view | Surge installed; generic always | `kAudioUnitProperty_CocoaUI` resolves (or generic fallback), child `NSView` embeds (`EmbeddedChildCount ≥ 1`) |

Anything that needs Surge **self-skips** when it is absent (like `ClapLiveTests`); the system-AU checks never
skip on macOS.

## 7. Tests (`tests/MidiSharp.Hosting.Tests` + the existing harness)

- **xUnit, macOS-only, self-skipping off-OS** (`OperatingSystem.IsMacOS()` gate, the established pattern):
  `AudioUnitFormatTests` (discovery surfaces `AULowpass`/`DLSMusicDevice`; `ScanFile` reads a bundle plist
  without loading code), `AudioUnitEffectTests` (load `AULowpass`, render, param sweep, state round-trip),
  `AudioUnitInstrumentTests` (Plan B — `DLSMusicDevice` note → non-silence).
- **The render path is xUnit-safe** — `AudioUnitRender` is not AppKit and needs no main thread — so unlike the
  Cocoa editor, the audio/instrument slices live entirely in xUnit (no separate harness).
- **Plan C editor** reuses the **main-thread `MacEditorHarness`** (AppKit ⇒ main thread): add an
  `EmbedReal("AU", …)` arm that loads an AU with a Cocoa view (Surge `.component`, else generic) and asserts a
  child `NSView` embeds — the exact shape already used for VST3/CLAP.

## 8. Build / toolchain

`dotnet` (net10.0 SDK on PATH) builds/tests/runs; **arm64**; `timeout`/`gtimeout` absent. The new project is
`src/MidiSharp.Hosting.AudioUnit/` added to the solution and referenced by the worker/registry on macOS. No
clang fixtures needed for Plan A/B (system AUs). Target: **0 warnings / 0 errors**; Linux/Windows suites
unaffected.

## 9. Correctness & realtime notes

- **The render shim is the bug-prone surface.** `AudioBufferList` is a variable-length struct (1 buffer inline +
  tail); allocate the 2-buffer stereo form once and reuse. The AU may ask the input callback for a different
  frame count than the block (it usually won't at our fixed `MaximumFramesPerSlice`); the callback must honor
  `inNumberFrames`/`inBusNumber` and clamp. Render-in-place: pointing `ioData` at our output channels asks the
  AU to render there; confirm AUs that need separate scratch handle a non-null `mData` (they do — that is the
  normal host contract).
- **Stereo/mono.** Set `ChannelsPerFrame = 2`; a mono-output AU (rare) is mirrored to channel 1 post-render.
- **ASBD must be set before `AudioUnitInitialize`** and `MaximumFramesPerSlice` before render; setting stream
  format after initialize fails. Order matters.
- **CF lifetime.** Every `CF*Create*`/copied `CFStringRef`/`CFURLRef`/plist is `CFRelease`d; the `ClassInfo`
  plist is only touched off the audio thread.
- **No thread-check trap.** AU has no Surge-style thread enforcement, and offline `AudioUnitRender` on the
  command thread is legitimate — the CLAP `start/stop_processing` issue does not recur.

## 10. Acceptance gate (measured, per slice)

**Plan A (effects):** discovery lists `AULowpass`; it loads and activates; rendering a block of non-zero input
through it **changes** the output (filter audibly acts); sweeping its cutoff parameter changes the result;
`SaveState`→`LoadState` round-trips; solution builds 0/0; other adapters unchanged. **Plan B (instruments):**
`DLSMusicDevice` reports `IsInstrument`, and a note-on via `MusicDeviceMIDIEvent` renders **non-silence**.
**Plan C (editor):** an AU with a Cocoa view embeds a child `NSView` through the existing `EditorSession`
(`EmbeddedChildCount ≥ 1`) on the main-thread harness; generic-view fallback covers AUs without custom UI.

## 11. Out of scope

- **AU v3 (`AUAudioUnit`, app extensions, `requestViewControllerWithCompletionHandler`)** — its own future
  track; v2 covers the installed base today.
- **AAX**, **LV2/DSSI** — separate parked formats (`docs/plugin-hosting-plan.md` §"later formats").
- **x86_64 macOS** — arm64 only, matching the rest of the macOS surface.
- **AU MIDI 2.0 / `MusicDeviceMIDIEventList`** — the legacy `MusicDeviceMIDIEvent` (3-byte) covers our
  `HostEvent` MIDI; the newer event-list API is a later enhancement.
- **Multi-bus / sidechain / non-stereo AUs** — stereo main in/out only, like the other adapters.

## 12. Open items to verify during implementation (Plan A Task 0 spike de-risks these first)

- **Confirm the property/scope/type constant values** against `AudioUnitProperties.h`/`CoreAudioTypes.h` before
  the adapter relies on them (values in §5.1 are from memory).
- **Prove the pull→push render shim end-to-end:** instantiate `AULowpass`, set non-interleaved float ASBD on
  both scopes, register the input callback, `AudioUnitRender` a block of known input, and confirm the output is
  the filtered signal (not silence, not a copy). This single spike validates the entire load-bearing design
  before Plan A's real code — the equivalent of the Cocoa `objc_msgSend` spike.
- **Confirm `AudioComponentFindNext` enumerates** Apple built-ins and that an `.component` Info.plist yields the
  3-tuple without `AudioComponentInstanceNew`.
- **Confirm the `ClassInfo` plist round-trips** through `CFPropertyListCreateData`/`…WithData` losslessly.
- **(Plan C)** Confirm `kAudioUnitProperty_CocoaUI` on Surge's AU yields a loadable view class, and pick the
  generic-view fallback API for Apple built-ins.

## 13. Plan breakdown (three shippable slices)

- **Plan A — AU effects core** (`2026-06-22-au-hosting-effects.md`): the project, the interop slice, the
  format (discovery), the plugin with the **render shim**, parameters, and state. Verified against Apple system
  effect AUs. Includes the **Task 0 spike**. *This is the risky, load-bearing slice.*
- **Plan B — AU instruments** (`2026-06-22-au-hosting-instruments.md`): `IsInstrument`, `MusicDeviceMIDIEvent`,
  no input bus. Verified against `DLSMusicDevice`. Small once Plan A lands.
- **Plan C — AU Cocoa editor** (`2026-06-22-au-hosting-editor.md`): `IPluginGui` over
  `kAudioUnitProperty_CocoaUI` + generic fallback, riding the existing `EditorSession`/Cocoa backend. Verified
  against Surge's `.component` on the main-thread harness.
