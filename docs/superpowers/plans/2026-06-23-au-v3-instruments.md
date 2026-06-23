# AU v3 Instruments (Plan B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** host **AU v3 instruments** (music-device `AUAudioUnit`s) — report `IsInstrument`, deliver MIDI via the
vended `scheduleMIDIEventBlock`, and render without an audio input bus. Verified against the **real v3 instrument
`AudMod`** (`aumu:audM:nLkL`, Unlikelyware — installed; `RequiresAsync` + `CanLoadInProcess`, so it loads
in-process, unlike the OOP-only v3 effects). A v2-wrapped `DLSMusicDevice` is a secondary sanity check.

**Architecture:** a music-device v3 AU has no input bus → no `pullInputBlock` and no input `AVAudioFormat`;
everything else (output format, the `renderBlock` path, params, state) is Plan A. The only new surface is MIDI:
each `Midi` `HostEvent` becomes a `scheduleMIDIEventBlock(eventSampleTime, cable, length, midiBytes)` call (a
block the AU vends, **invoked** via `AuBlocks`) immediately before `InvokeRender`.

**Spec:** `docs/superpowers/specs/2026-06-23-au-v3-hosting-design.md` (§5.5, §6, §10). **Prerequisite: Plan A.**

**Tech Stack:** C# (net10.0), AudioToolbox/AVFoundation `objc_msgSend` + blocks, xUnit. **arm64, macOS-only.**

## Global Constraints

- Same as Plan A: `dotnet`, net10.0, **0/0**, dependency-free, additive (no v2-path / engine change),
  realtime-clean `Process`, macOS-only tests self-skip off-OS.

## Task 1 — Instrument detection & no-input activation

- [ ] In `AudioUnitV3Plugin`, branch on `IsInstrument` (component type ∈ {`'aumu'`,`'augn'`}): set **only** the
      output bus `AVAudioFormat`; register **no** `pullInputBlock` and no input format. Otherwise identical to
      Plan A's activate.
- [ ] **Test** `Audmod_v3_instrument_loads` (macOS-only): the real v3 **`AudMod`** (`aumu:audM:nLkL`)
      async-loads (in-process), activates, reports `IsInstrument`; a v2-wrapped `DLSMusicDevice` arm is a
      secondary check. SKIPs when no v3 instrument is installed. Commit.

## Task 2 — MIDI delivery (`scheduleMIDIEventBlock`)

- [ ] Cache the AU's `scheduleMIDIEventBlock` (a vended block) in Activate. In `Process`, before `InvokeRender`,
      for each `Midi` `HostEvent` invoke it via `AuBlocks` with `(eventSampleTime = SampleOffset, cable = 0,
      length = 3, midiBytes = {Status, Data1, Data2})`. No managed allocation on this path.

## Task 3 — Verify a note renders & acceptance

- [ ] **Acceptance gate (spec §10, Plan B):** the real v3 **`AudMod`** reports `IsInstrument`; a note-on
      (`0x90, 60, 100`) via `scheduleMIDIEventBlock` renders **non-silent** output (near-silent before the note).
      (`AudMod` is in-process-capable, so this also covers the in-process v3 load path the effects can't.)
- [ ] **Test** asserts the above (self-skips off macOS). Solution **0/0**; v2 path + other suites unchanged.
- [ ] Update `docs/plugin-hosting-plan.md` and `CHANGELOG.md`. Commit. **Do not merge/push unless asked.**
