# AU Effects Core (Plan A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** host **Audio Unit (AU v2) effects** on macOS through a new `MidiSharp.Hosting.AudioUnit` adapter —
discover via the component registry, load, run audio through the **pull→push render shim**, expose parameters,
and round-trip state. Verified against Apple's always-present system effect AUs (`AULowpass`, `AUDelay`,
`AUMatrixReverb`) — **no third-party plugin and no clean-room fixtures**.

**Architecture:** a new net10.0, macOS-only adapter implementing `IPluginFormat` + `IHostedPlugin` over the
AudioToolbox C API (`AudioComponentFindNext` → `AudioComponentInstanceNew` → `AudioUnitRender`). AU is *pull*
(the AU pulls input from a host-registered `AURenderCallback`); our engine is *push* (`Process(input, output,
events)`). The adapter bridges the two with an `[UnmanagedCallersOnly]` input callback that feeds from the
block's stashed input pointers while the output `AudioBufferList` points at our output channels. Audio is
**non-interleaved float32**, so it reads/writes `PlanarBuffers` directly (no `PlanarBridge`).

**Spec:** `docs/superpowers/specs/2026-06-22-au-hosting-design.md` (§5.1–5.4, §6, §9, §10, §12). **Task 0 (the
render-shim spike) gates the rest** — do not write adapter code until the shim is proven.

**Tech Stack:** C# (net10.0), AudioToolbox/CoreFoundation P/Invoke, xUnit v3. **arm64, macOS-only.**

## Global Constraints

- **Build/test/run with `dotnet`** (net10.0 SDK on PATH). `timeout`/`gtimeout` absent.
- **Target framework:** `net10.0`; build clean **0 warnings / 0 errors**.
- **Dependency-free:** only `AudioToolbox`/`CoreFoundation` system-framework P/Invoke; no new package refs.
- **Additive only:** no change to the engine, synth, or the CLAP/VST2/VST3/LADSPA adapters. The registry
  registers AU **guarded by `OperatingSystem.IsMacOS()`**; Linux/Windows builds and suites stay unchanged (0/0).
- **`Process` is realtime-clean:** no managed alloc, no locks; the render callback is `[UnmanagedCallersOnly]`.
- **macOS-only tests self-skip off-OS** via `Assert.SkipWhen(!OperatingSystem.IsMacOS(), …)`; system-AU checks
  never skip on macOS.

## Task 0 — Render-shim spike (de-risk the load-bearing design)  — **gate** ✅ SPIKE PASS 2026-06-22

- [x] In a throwaway console (net10.0), P/Invoke AudioToolbox to: `AudioComponentFindNext` for
      `AULowpass` (`type 'aufx'`, subtype `'lpas'`, manufacturer `'appl'`) → `AudioComponentInstanceNew`.
- [x] Set stereo **non-interleaved float32** ASBD on scope-Output **and** scope-Input;
      `kAudioUnitProperty_MaximumFramesPerSlice`; register an `AURenderCallbackStruct` whose proc fills `ioData`
      from a known input buffer (a full-scale sine). `AudioUnitInitialize`.
- [x] `AudioUnitRender` 512-frame blocks into a 2-buffer `AudioBufferList` pointing at managed output arrays;
      assert the output is the **filtered** input (non-silent, and *different* from the input — the lowpass acts).
- [x] Confirm the §5.1 constant values against runtime behavior; correct the spec if any differ.

> **Outcome (2026-06-22, scratchpad `au-spike`, this arm64 Mac):** SPIKE PASS. AULowpass passed 200 Hz at
> 0.98× (out RMS 0.3465 vs input 0.3536 — proves input was pulled through the callback **and** output captured)
> and attenuated 18 kHz to −27 dB (out RMS 0.0146 — proves the AU genuinely filtered). Every AudioToolbox call
> returned `0`. **All §5.1 constants verified correct as transcribed — no spec correction needed:**
> `StreamFormat=8`, `MaximumFramesPerSlice=14`, `SetRenderCallback=23`; scopes `Global=0`/`Input=1`/`Output=2`;
> non-interleaved native float32 flags `=41` (`IsFloat|IsPacked|IsNonInterleaved`); the ASBD, 2-buffer
> `AudioBufferList`, and 72-byte `AudioTimeStamp` (incl. embedded `SMPTETime`) layouts; and the
> `[UnmanagedCallersOnly(Cdecl)]` input callback marshalling. The pull→push render shim design is validated;
> Task 1 may proceed. (Non-interleaved ASBD: per-channel `mBytesPerPacket = mBytesPerFrame = 4`,
> `mChannelsPerFrame = 2`, `mFramesPerPacket = 1`.)

## Task 1 — Project + interop slice (`AudioUnitAbi.cs`)  ✅ 2026-06-22

- [x] Create `src/MidiSharp.Hosting.AudioUnit/MidiSharp.Hosting.AudioUnit.csproj` (net10.0), add to the solution
      (`MidiSharp.slnx`), reference `MidiSharp.Hosting`.
- [x] `AudioUnitAbi.cs`: the structs, function P/Invokes, and constants from spec §5.1, with the **spike-verified**
      constant values. Stereo `StereoBufferList` specialization of the variable-length `AudioBufferList`. OSType
      `FourCC` helper. (Parameter/state/MIDI/editor ABI deferred to their own tasks — Tasks 4–5, Plans B/C.)
- [x] Builds **0/0** — both the project alone and the whole solution. Single assembly compiled everywhere, called
      only on macOS (registry guard lands in Task 6), mirroring the platform-guarded EditorHost backends.

## Task 2 — Format & discovery (`AudioUnitFormat.cs`)  ✅ 2026-06-22 (done with Task 3 — coupled via `Load`)

- [x] `IPluginFormat` with `Name => "AU"`, the component `DefaultSearchPaths`, `EnumerateFiles` (sorted
      `*.component` bundles), and `ScanFile` reading `Contents/Info.plist`'s `AudioComponents` array via CF plist
      (**no native code loaded**) → a `PluginDescriptor` per entry carrying the type:subtype:manufacturer triple
      in `Id` and `IsInstrument = type ∈ {'aumu','augn'}`.
- [x] `Scan` walks `AudioComponentFindNext` (the real AU discovery; surfaces Apple built-ins that aren't files on
      disk) unioned with the on-disk `ScanFile` pass, de-duplicated by triple. `AULowpass`/`DLSMusicDevice` appear.
- [x] `Load` → parse the triple → `AudioComponentFindNext` → `AudioComponentInstanceNew` → `AudioUnitPlugin`.
- [x] **Test** `AudioUnitTests.Discovers_apple_system_audio_units` (macOS-only): discovery surfaces `AULowpass`
      **and** `DLSMusicDevice` (instrument). (32 AUs scanned on this Mac.)

> **Note:** a new `CoreFoundation.cs` interop slice was needed for the Info.plist parse (reused later by Task 5
> state + Plan C editor). **Bug found & fixed during bring-up:** `ToManaged` must type-check with `CFGetTypeID`
> before `CFStringGetCString` — some plist values are CFNumber/CFData, and stringifying them traps as an
> Obj-C `NSException` (SIGABRT). Dict/array accessors are likewise type-guarded.

## Task 3 — Plugin: activate + render shim (`AudioUnitPlugin.cs`)  ✅ 2026-06-22

- [x] `Activate`: set output (+ input, for effects) ASBD (stereo non-interleaved float32),
      `MaximumFramesPerSlice`, register the input `AURenderCallbackStruct` (`RefCon = GCHandle(this)`),
      `AudioUnitInitialize`.
- [x] `Process`: stash `input.Channel(0/1)` + frame count; point the output `StereoBufferList` at
      `output.Channel(c)`; bump `SampleTime`; `AudioUnitRender`. The `[UnmanagedCallersOnly]` input callback
      serves the stashed input, honoring `inNumberFrames` and zero-padding any shortfall.
- [x] `Deactivate`/`Dispose`: `AudioUnitUninitialize` + `AudioComponentInstanceDispose`; free the `GCHandle`.
      (Buffer list / timestamp are stack locals per block — no heap to free.)
- [x] **Test** `AudioUnitTests.Loads_aulowpass_and_filters_audio_through_the_render_shim` (macOS-only): a 200 Hz
      tone passes (out RMS 0.3465), an 18 kHz tone is attenuated (0.0146) — **identical to the Task 0 spike**,
      proving input was pulled and output captured through the real `IHostedPlugin`. Parameters (Task 4) and
      state (Task 5) are empty-but-correct for now (`Parameters` empty, `SaveState` → `[]`).

## Task 4 — Parameters  ✅ 2026-06-22

- [x] `BuildParameters` (after `AudioUnitInitialize`) reads `PropParameterList` (via `GetPropertyInfo` for the
      byte count, then `GetProperty`) + `PropParameterInfo` per id → `PluginParameter[]` + parallel
      `_paramIds` (`AudioUnitParameterID`). Name from `CfNameString` (CF) falling back to the inline `char[52]`;
      `isStepped` from `Unit ∈ {Indexed, Boolean}`. `GetParameter`/`SetParameter` via `AudioUnitGetParameter`/
      `AudioUnitSetParameter` (scope Global), normalized; param `HostEvent`s apply in `Process` with
      `SampleOffset` as the buffer offset (realtime-safe).
- [x] **Test** `Sweeping_the_cutoff_parameter_changes_the_filter_output`: 1 kHz passes at the default cutoff,
      then dropping the cutoff parameter to its minimum attenuates it (< 0.5×). Hosting suite 31→32; solution 0/0.

## Task 5 — State  ✅ 2026-06-22

- [x] `SaveState`: `AudioUnitGetProperty(ClassInfo)` (get-rule dict, we own it) → `CoreFoundation.ToData`
      (`CFPropertyListCreateData` binary plist) → bytes; `[]` when absent. `LoadState`: bytes →
      `CreatePropertyList` → type-check it's a dictionary → `AudioUnitSetProperty(ClassInfo)`. Every CF object
      released. `CoreFoundation.cs` grew the `CFPropertyListCreateData`/`CFDataGetLength`/`CFDataGetBytePtr` calls.
- [x] **Test** `State_round_trips_through_classinfo`: set the cutoff to 0.7, `SaveState` (non-empty), move it to
      0.1, `LoadState`, assert the cutoff is restored (~0.7, within 0.05). Hosting suite 32→33; solution 0/0.

## Task 6 — Registry wiring + acceptance

- [ ] Register `AudioUnitFormat` in the `PluginRegistry`/worker **guarded by `OperatingSystem.IsMacOS()`** so AU
      plugins surface in discovery alongside CLAP/VST on macOS only.
- [ ] **Acceptance gate (spec §10, Plan A):** discovery lists `AULowpass`; load+activate; render changes output;
      param sweep changes output; state round-trips; **solution builds 0/0**; Linux/Windows suites unchanged.
- [ ] Update `docs/plugin-hosting-plan.md` (AU effects done) and `CHANGELOG.md` (`[Unreleased]` → Added).
      Commit. **Do not merge/push unless asked.**
