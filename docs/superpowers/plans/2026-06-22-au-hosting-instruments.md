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

## Task 1 — Instrument detection & no-input activation  ✅ 2026-06-22 (already in place from Plan A Task 3)

- [x] `AudioUnitPlugin.Activate` branches on `IsInstrument` (`type ∈ {'aumu','augn'}`): instruments set **only**
      the output ASBD and register **no** input ASBD / `AURenderCallback`; otherwise identical to the effect path.
- [x] `IHostedPlugin.IsInstrument => Descriptor.IsInstrument` (true for `DLSMusicDevice`).

## Task 2 — MIDI delivery (`MusicDeviceMIDIEvent`)  ✅ 2026-06-22

- [x] Added `MusicDeviceMIDIEvent` to `AudioUnitAbi`. The `Process` event loop now, alongside param automation,
      delivers each `Midi` `HostEvent` (for instruments) via `MusicDeviceMIDIEvent(au, Status, Data1, Data2,
      SampleOffset)` — sample-accurate, no managed allocation. (Gated on `IsInstrument`; pure effects don't take
      MIDI.)

## Task 3 — Verify a note renders & acceptance  ✅ 2026-06-22

- [x] **Acceptance gate (spec §10, Plan B):** `DLSMusicDevice` loads, reports `IsInstrument`, and a note-on
      (`0x90, 60, 100`) renders **audible** output (RMS ≈ 0.03 vs near-silence before the note) — the note drove
      the sound, through the built-in soundbank with no explicit bank load.
- [x] **Test** `Dls_instrument_renders_a_note` (self-skips off macOS). Hosting suite 33→34; solution 0/0;
      non-macOS unchanged.

**Plan B (AU instruments) is complete.** Next: Plan C (Cocoa editor).
