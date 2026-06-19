# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/), and this
project adheres to [Semantic Versioning](https://semver.org/).

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

[0.10.0]: https://github.com/lxman/MidiSharp/releases/tag/v0.10.0
[0.9.0]: https://github.com/lxman/MidiSharp/releases/tag/v0.9.0
