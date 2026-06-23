# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/), and this
project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **AU v3 (`AUAudioUnit`) audio hosting on macOS.** Host modern **AU v3 effects and instruments** — which the
  component registry delivers over Apple's bridge and which must be instantiated *asynchronously* (effects
  out-of-process) — through the **same `AudioUnitPlugin`** as AU v2. The only v3-specific code is an async-load
  branch in `Load`: `AudioComponentInstantiate` driven by one hand-built Obj-C completion block, with the v2
  C API (render shim, parameters, `kAudioUnitProperty_ClassInfo` state, `MusicDeviceMIDIEvent`) working
  unchanged over the bridge — out-of-process render latency is ~18 µs/block (negligible). Verified against real
  v3 plugins (`DimChorus` effect OOP, `AudMod` instrument in-process). The v3 *editor* (which needs the
  `AUAudioUnit` view-controller path) is not yet adapted; AAX remains parked.

## [0.12.0] - 2026-06-22

### Added

- **Audio Unit (AU v2) hosting on macOS (`MidiSharp.Hosting.AudioUnit`).** Host **AU effects and instruments**,
  including their **native Cocoa editor** — discovered via the system component registry (and on-disk
  `.component` bundles), run through the engine as any other plugin, with parameters, state
  (`kAudioUnitProperty_ClassInfo`), MIDI for instruments (`MusicDeviceMIDIEvent`), and the unit's own Cocoa view
  (`kAudioUnitProperty_CocoaUI`, with an `AUGenericView` fallback) embedded through the existing editor host. AU
  is a *pull* format (the unit pulls input from a host render callback); a small shim bridges that to the
  engine's *push* processing, with audio kept in non-interleaved float so it maps straight onto the planar bus.
  AU editor windows use a neutral, appearance-aware background so transparent views with dark controls stay
  legible. Registered alongside CLAP/VST on macOS only; discovered in-process (crash-safe) even under the
  sandbox. AU v3 and AAX remain unadapted.

## [0.11.0] - 2026-06-22

A plugin-hosting subsystem — load and run third-party audio plugins inside the mixer — and native plugin
editors on Linux, Windows, and macOS.

### Added

- **Plugin hosting (`MidiSharp.Hosting`).** Host external **CLAP, VST2, VST3, and LADSPA** plugins as both
  **effects** (drop into any `ProcessorChain`/insert rack next to the built-in EQ/limiter/gain) and
  **instruments** (an alternative sound source to the synth, fed sample-accurate note events and carrying the
  full channel strip). The format-agnostic core holds no P/Invoke — each format's native interop lives in its
  own adapter (`MidiSharp.Hosting.{Clap,Vst2,Vst3,Ladspa}`) — and a no-GC/no-lock `PlanarBridge` shuttles the
  engine's interleaved stereo to and from each plugin's planar buffers. ABIs are transcribed clean-room (no
  vendor SDK headers).
- **Out-of-process sandboxing (`MidiSharp.Hosting.Sandbox` + `MidiSharp.Hosting.Worker`).** Discovery and load
  run in worker processes, so a plugin that crashes on scan or load takes down only its worker, not the server;
  per-file scan resume, a hung-plugin watchdog, and audio/parameter/state proxying over a shared-memory ring.
  On by default in the web player (`MIDISHARP_SANDBOX=0` disables it). Plugin state persists into saved setups.
- **Native plugin editors (`MidiSharp.Hosting.EditorHost`).** A plugin's own editor opens as a real OS window
  — hosted in the sandbox worker, opened from the web player — behind one per-OS backend: **X11** (Linux),
  **Win32** (Windows), and **Cocoa** (macOS/arm64). Each is pure platform P/Invoke (`libX11` /
  `user32`+`gdi32` / `libobjc`+AppKit) with its own run loop (`poll` / `MsgWaitForMultipleObjectsEx` /
  `NSApp`), embedding the plugin's child window and driving its timers/fds. Verified by embedding real plugins
  (u-he Podolski on Windows; Surge XT VST3 + CLAP on macOS).
- **macOS plugin discovery** now also searches the system `/Library/Audio/Plug-Ins/{VST3,CLAP}` directories and
  resolves `.clap` **bundles** (`Contents/MacOS/<name>`), not just per-user flat files.

### Changed

- The web player's insert racks and the per-part instrument selector can bind hosted plugins alongside the
  built-in synth and DSP.
- The editor-host backends are organized into per-OS folders/namespaces
  (`MidiSharp.Hosting.EditorHost.{Linux,Windows,MacArm}`); shared windowing-agnostic code stays in the root.
- 320 tests across the suite (adds the Hosting suite); CI green on Linux, Windows, and macOS; platform-specific
  editor/plugin tests self-skip off their OS.

### Fixed

- **CLAP host thread/order compliance**, surfaced by hosting Surge XT (a strict, self-checking plugin); both
  issues were pre-existing and platform-agnostic. `start_processing`/`stop_processing` (CLAP `[audio-thread]`
  calls) are now reported on the audio thread — the lock-step worker distinguishes thread roles by context, so
  they enter the same audio-thread bracket as `process()`. And the host no longer queries `gui.size()` before
  `gui.create()`: the editor window opens at a provisional size and resizes to the plugin's real size after
  create (before it is mapped) and again after show.
- **MidiSharp.OwnAudio output-device selection.** Choosing an output device now actually opens *that* device.
  The wrapper applies the selection by index after engine init — the backend ignores the device id at init, and
  on Windows each endpoint is enumerated once per host API (MME/DirectSound/WASAPI/WDM-KS) so names aren't
  unique. Engine bring-up is now transactional: a failed open rolls back and surfaces a clear, actionable error
  instead of leaving a poisoned engine that crashes the next play.
- **MidiSharp.OwnAudio per-device sample rate.** The pipeline now runs at the device's native rate instead of a
  hardcoded 48 kHz. Exact-format backends (WASAPI / WDM-KS do no implicit resampling) refused to open a device
  whose native rate differed (e.g. a 44.1 kHz endpoint), failing with `paInvalidSampleRate`; the rate is now
  queried up front (PortAudio device info, falling back to MiniAudio's converter) and the rate-dependent graph
  is built to match.
- **MidiSharp.OwnAudio teardown race.** A teardown gate drains any in-flight audio callback before the mixer is
  stopped, preventing a use-after-free of the memory-mapped SoundFont (an `AccessViolationException` that took
  down the whole process) when a callback raced shutdown — e.g. rapid device switching during playback.

### Notes

- **AU (Audio Units)** is the one major plugin format not yet adapted (macOS-only); **AAX** is parked.

## [0.10.0] - 2026-06-19

A mixing-and-effects layer on top of the renderer — without touching the spec-faithful core.

### Added

- **`MidiSharp.Dsp`** — a new, decoupled buffer-processing DSP library (bundled into `MidiSharp.Player`,
  no dependency on the synth or MIDI): `IAudioProcessor`, a lock-free reorderable `ProcessorChain`, a
  clean-room RBJ `BiquadFilter` (6 response types), a stereo `ParametricEq`, a stereo-linked brickwall
  `LimiterProcessor`, and `GainProcessor`.
- **Per-instrument mixer in the synth** — `Synthesizer.GetInstrumentMix(bank, program)` exposes a live
  `InstrumentMix` (gain trim / pan offset / mute / solo / reverb + chorus sends). The gain *augments* the
  file's own CC7/CC11 automation rather than replacing it; an untouched mixer is **bit-identical** to the
  previous engine.
- **Per-instrument insert effects (Tier-2)** — `IInstrumentInsert` lets the host shape one instrument's
  signal (its voices sum into a private bus → the insert → master). Instruments with no insert are
  bit-identical. The synth stays decoupled from `MidiSharp.Dsp` through this hook.
- **Web player is now a live mixing console** — one channel strip per MIDI **track** (source-font
  substitution + fader/pan/sends/mute/solo + a drag-and-drop EQ/limiter insert rack), a master strip with
  its own EQ/limiter rack and output fader, all live over `/api/mix`, `/api/insert`, `/api/master`.
  Setups now persist per-track mix + inserts + master.
- **`MidiSharp.Demo --limiter [ceilingDb]`** — inserts a master brickwall limiter on the WAV-render path.

### Changed

- The web mixer keys each strip on the source MIDI **track** (the part), so a performer keeps one fader as
  their program changes and two tracks sharing a program get independent faders.
- Consolidated the per-assembly `IsExternalInit` shim into a single shared source file compiled across the
  netstandard2.1 libraries (`build/IsExternalInit.cs` via `Directory.Build.props`).
- 238 unit tests (up from 209), including the mixer/insert engine and the DSP effects; CI green on Linux,
  Windows, and macOS; 0 build warnings.

### Notes

- The synth itself is unchanged in what it renders — all mixing/EQ/limiting is caller-side DSP, kept out of
  the renderer on purpose.
- Setups saved by 0.9.0 still load: their instrument substitutions apply, but pre-existing per-program
  gains don't carry to the new per-track faders (re-balance on the strips).

## [0.9.0] - 2026-06-19

First public release. 🎹

A cross-platform, pure-managed **C# software synthesizer** that renders Standard MIDI
Files through SoundFont presets — **SF2, SF3, SFZ, and DLS**, all loaded into one
common in-memory representation. Built on a spec-compliant MIDI 1.0 / GM / GM2 / GS /
XG protocol surface and an SF2-style generator/modulator engine validated against
fluidsynth.

> **Note on the package name:** the bare `MidiSharp` ID on nuget.org belongs to an
> unrelated older library, so this ships as **`MidiSharp.Player`** (the assemblies and
> namespaces are still `MidiSharp.*`).

### Install

```bash
dotnet add package MidiSharp.Player     # full stack — render MIDI to float buffers / WAV
dotnet add package MidiSharp.OwnAudio    # add this for live cross-platform speaker output
```

`MidiSharp.Player` is **self-contained** (no external dependencies) and targets
**netstandard2.1** — usable from .NET Core 3.x, .NET 5–10, Unity, and MAUI.
`MidiSharp.OwnAudio` targets net10.0 and adds the OwnAudioSharp output backend.

### What's in it

**Multi-format soundfont loading** — SF2/SF3/SFZ/DLS flatten into one `SoundBank` IR;
the synth never branches on source format.

- **SF2** — the primary, fluidsynth-validated path; memory-mapped sample source; 16- and 24-bit (sm24).
- **SF3** — lazy Vorbis decoding with an LRU cache.
- **SFZ** — extensive v1/v2/ARIA coverage: multi-type filters (incl. low/high-shelf and
  peaking), a second filter, peaking-EQ bands, generic LFOs (incl. sample-and-hold and
  stepped), flex envelopes, velocity/key/CC crossfades, keyswitch, round-robin, CC gates.
  Coverage is data-driven — every opcode used across a **2027-font test collection** is handled.
- **DLS** — Level 1/2 RIFF with full articulation translation (EG1/EG2, mod/vibrato LFO, filter, sends, MIDI routes).

**Synthesis engine** — all 10 SF2 default modulators, EMU8k attenuation, 7-point sinc
interpolation, per-voice envelopes + LFOs, RBJ biquad filters in every response type,
FDN reverb (Jot) + stereo chorus, sample looping with loop-until-release.

**MIDI protocol** — channel messages, the common CC set, RPN/NRPN (incl. GS drum + XG
families), GM/GM2/GS/XG SysEx, SMF meta (tempo, lyrics/markers via callback). Sample-
accurate event timing driven from the audio callback.

**Instrument substitution** (`MidiSharp.PatchMap`) — list the instruments a song uses
and swap any of them for one cherry-picked from another font, without touching the
song's sequencing.

### Quality

- Validated A/B vs fluidsynth on GS-heavy content: within ±3.5 dB worst-case across the
  spectrum, bass essentially identical (±1 dB).
- **209 unit tests**, green on **Linux, Windows, and macOS** (CI), 0 build warnings.

### Scope (what's intentionally out, for now)

- Mastering / output EQ / limiting (the synth renders the spec; mastering is the caller's job)
- The SFZ `<effect>` DSP-bus engine and wavetable-oscillator synthesis
- Hardware/OS MIDI-port output (this renders *audio*, it's not a MIDI router)
- SoundFont authoring/writing; MIDI 2.0 / UMP

This is a **0.x** release: the SF2 path is rock-solid and the API is stable enough to
build on, but SF3/SFZ/DLS are newer and the surface may still shift before 1.0.

### Credits

The Ogg Vorbis decoder (`MidiSharp.Audio.Vorbis`) is a maintained fork of
[NVorbis](https://github.com/NVorbis/NVorbis) v0.10.5 by **Andrew Ward** (MIT) — see
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

**License:** MIT.

[Unreleased]: https://github.com/lxman/MidiSharp/compare/v0.12.0...HEAD
[0.12.0]: https://github.com/lxman/MidiSharp/compare/v0.11.0...v0.12.0
[0.11.0]: https://github.com/lxman/MidiSharp/compare/v0.10.0...v0.11.0
[0.10.0]: https://github.com/lxman/MidiSharp/releases/tag/v0.10.0
[0.9.0]: https://github.com/lxman/MidiSharp/releases/tag/v0.9.0
