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

## Task 0 — Render-shim spike (de-risk the load-bearing design)  — **gate**

- [ ] In a throwaway console (`/tmp/au-spike`, net10.0), P/Invoke AudioToolbox to: `AudioComponentFindNext` for
      `AULowpass` (`type 'aufx'`, subtype `'lpas'`, manufacturer `'appl'`) → `AudioComponentInstanceNew`.
- [ ] Set stereo **non-interleaved float32** ASBD on scope-Output **and** scope-Input;
      `kAudioUnitProperty_MaximumFramesPerSlice`; register an `AURenderCallbackStruct` whose proc fills `ioData`
      from a known input buffer (e.g. a unit impulse / full-scale sine). `AudioUnitInitialize`.
- [ ] `AudioUnitRender` one 512-frame block into a 2-buffer `AudioBufferList` pointing at managed output arrays;
      assert the output is the **filtered** input (non-silent, and *different* from the input — the lowpass acts).
- [ ] Confirm the §5.1 constant values (`StreamFormat=8`, `SetRenderCallback=23`, `MaximumFramesPerSlice=14`,
      scopes, format flags) against the actual headers / runtime behavior; correct the spec's §5.1/§12 table if
      any differ. **Record SPIKE PASS (and any corrected constants) before Task 1.**

## Task 1 — Project + interop slice (`AudioUnitAbi.cs`)

- [ ] Create `src/MidiSharp.Hosting.AudioUnit/MidiSharp.Hosting.AudioUnit.csproj` (net10.0), add to the solution,
      reference `MidiSharp.Hosting`.
- [ ] `AudioUnitAbi.cs`: the structs, function P/Invokes, and constants from spec §5.1, with the **spike-verified**
      constant values. `AudioBufferList` allocated in its 2-buffer stereo form. OSType helper (`'aufx'` →
      big-endian `uint`).
- [ ] Builds 0/0 on macOS; the project is excluded/no-op on non-macOS (the registry won't reference it there).

## Task 2 — Format & discovery (`AudioUnitFormat.cs`)

- [ ] `IPluginFormat` with `Name => "AU"`, the component `DefaultSearchPaths`, `EnumerateFiles` (sorted
      `*.component` bundles), and `ScanFile` reading `Contents/Info.plist`'s `AudioComponents` array via CF plist
      (**no native code loaded**) → a `PluginDescriptor` per entry carrying the type/subtype/manufacturer 3-tuple
      and `IsInstrument = type ∈ {'aumu','augn'}`.
- [ ] `Scan` also walks `AudioComponentFindNext` to surface system AUs (no on-disk bundle), de-duplicated by
      3-tuple, so `AULowpass` appears.
- [ ] `Load` → `AudioComponentFindNext`(3-tuple) → `AudioComponentInstanceNew` → `AudioUnitPlugin`.
- [ ] **Test** `AudioUnitFormatTests` (macOS-only): discovery surfaces `AULowpass` **and** `DLSMusicDevice`;
      `ScanFile` of an installed `.component` (if any) yields a descriptor without instantiating. Commit.

## Task 3 — Plugin: activate + render shim (`AudioUnitPlugin.cs`)

- [ ] `Activate`: set output/input ASBD (stereo non-interleaved float32), `MaximumFramesPerSlice`, register the
      input `AURenderCallbackStruct` (`RefCon = GCHandle(this)`), `AudioUnitInitialize`; allocate the reusable
      `AudioBufferList` + `AudioTimeStamp`.
- [ ] `Process`: stash `input.Channel(0/1)` + frame count; point `ioData.mBuffers[c].mData` at
      `output.Channel(c)`; bump `mSampleTime`; `AudioUnitRender`. The `[UnmanagedCallersOnly]` input callback
      fills `ioData` from the stashed input, honoring `inNumberFrames`/`inBusNumber`. Mirror mono→stereo.
- [ ] `Deactivate`/`Dispose`: `AudioUnitUninitialize` + `AudioComponentInstanceDispose`; free the buffer list;
      free the `GCHandle`.
- [ ] **Test** `AudioUnitEffectTests` (macOS-only): load `AULowpass`, render a non-zero block, assert the output
      is non-silent and differs from the input (filter acts). Commit.

## Task 4 — Parameters

- [ ] Build the table from `kAudioUnitProperty_ParameterList` + `…ParameterInfo` → `PluginParameter[]` +
      parallel `AudioUnitParameterID[]`. `GetParameter`/`SetParameter` via `AudioUnitGetParameter`/
      `AudioUnitSetParameter` (scope Global), normalized; sample-accurate param `HostEvent`s pass `SampleOffset`
      as `bufferOffsetInFrames`.
- [ ] **Test:** sweeping `AULowpass` cutoff changes the rendered output. Commit.

## Task 5 — State

- [ ] `SaveState`: `AudioUnitGetProperty(ClassInfo)` → `CFPropertyListCreateData(binary)` → bytes (empty if
      absent). `LoadState`: bytes → `CFPropertyListCreateWithData` → `AudioUnitSetProperty(ClassInfo)`. `CFRelease`
      everything.
- [ ] **Test:** set a parameter, `SaveState`, change it, `LoadState`, assert the parameter restored. Commit.

## Task 6 — Registry wiring + acceptance

- [ ] Register `AudioUnitFormat` in the `PluginRegistry`/worker **guarded by `OperatingSystem.IsMacOS()`** so AU
      plugins surface in discovery alongside CLAP/VST on macOS only.
- [ ] **Acceptance gate (spec §10, Plan A):** discovery lists `AULowpass`; load+activate; render changes output;
      param sweep changes output; state round-trips; **solution builds 0/0**; Linux/Windows suites unchanged.
- [ ] Update `docs/plugin-hosting-plan.md` (AU effects done) and `CHANGELOG.md` (`[Unreleased]` → Added).
      Commit. **Do not merge/push unless asked.**
