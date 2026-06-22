# AU Instruments (Plan B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** host **Audio Unit (AU v2) instruments** (music-device AUs) through the `MidiSharp.Hosting.AudioUnit`
adapter: report `IsInstrument`, deliver MIDI via `MusicDeviceMIDIEvent`, and render without an audio input bus.
Verified against Apple's always-present **`DLSMusicDevice`** (the built-in General-MIDI synth).

**Architecture:** a music-device AU (`type 'aumu'` or generator `'augn'`) has no audio input — so no input
render callback and no input ASBD — and is driven by MIDI rather than audio-in. Everything else (output ASBD,
render shim output side, parameters, state) is exactly Plan A. The only new surface is MIDI delivery: each
`HostEvent` of kind `Midi` becomes a `MusicDeviceMIDIEvent(au, status, data1, data2, sampleOffset)` call
immediately before `AudioUnitRender`.

**Spec:** `docs/superpowers/specs/2026-06-22-au-hosting-design.md` (§5.5, §6, §10). **Prerequisite: Plan A**
(the adapter, the render shim output path, parameters, state) must be merged.

**Tech Stack:** C# (net10.0), AudioToolbox P/Invoke, xUnit v3. **arm64, macOS-only.**

## Global Constraints

- Same as Plan A: `dotnet`, net10.0, **0/0**, dependency-free, additive (no engine/other-adapter change),
  realtime-clean `Process`, macOS-only tests self-skip off-OS. System-AU checks never skip on macOS.

## Task 1 — Instrument detection & no-input activation

- [ ] In `AudioUnitPlugin`, branch on `IsInstrument` (from the descriptor, `type ∈ {'aumu','augn'}`): for
      instruments, set **only** the output ASBD; **do not** set an input ASBD or register the input
      `AURenderCallback`. `Activate`/`MaximumFramesPerSlice`/`AudioUnitInitialize` otherwise identical to Plan A.
- [ ] `IHostedPlugin.IsInstrument => true` for these.
- [ ] **Test** `AudioUnitInstrumentTests` (macOS-only): `DLSMusicDevice` loads, activates, and reports
      `IsInstrument`. Commit.

## Task 2 — MIDI delivery (`MusicDeviceMIDIEvent`)

- [ ] Add `MusicDeviceMIDIEvent` to `AudioUnitAbi`. In `Process`, before `AudioUnitRender`, iterate the block's
      `HostEvent`s of kind `Midi` and call `MusicDeviceMIDIEvent(au, e.Status, e.Data1, e.Data2, (uint)e.SampleOffset)`
      (sample-accurate offset). Param `HostEvent`s still go through `AudioUnitSetParameter` (Plan A path).
- [ ] No managed allocation on this path (it's the realtime hot path).

## Task 3 — Verify a note renders & acceptance

- [ ] **Acceptance gate (spec §10, Plan B):** load `DLSMusicDevice`, activate, deliver a note-on
      (`0x90, 60, 100`) at offset 0, render several blocks, and assert the output is **non-silent** (RMS above a
      small threshold); a following note-off silences it. `IsInstrument` is true.
- [ ] **Test** asserts the above (self-skips off macOS). Solution builds **0/0**; Linux/Windows suites
      unchanged.
- [ ] Update `docs/plugin-hosting-plan.md` (AU instruments done) and `CHANGELOG.md`. Commit. **Do not merge/push
      unless asked.**
