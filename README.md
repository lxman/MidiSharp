# MidiSharp

A cross-platform, pure-managed C# software synthesizer that renders MIDI files through SoundFont 2 (.sf2) presets. Built around a fully spec-compliant MIDI 1.0 / GM / GM2 / GS / XG protocol implementation and an SF2 generator/modulator engine validated against fluidsynth.

## Quick start

```bash
# Live playback (use single quotes around files with ! or spaces)
dotnet run --project samples/MidiSharp.Demo -- \
  '<path-to>.mid' '<path-to>.sf2'
# Controls: Space pause/resume, S reset, Q quit

# Render to WAV instead of live playback
dotnet run --project samples/MidiSharp.Demo -- \
  '<path-to>.mid' '<path-to>.sf2' /tmp/out.wav
```

Tested daily against `GeneralUser-GS.sf2` and `TyrolandSFX.sf2`. End-to-end A/B vs fluidsynth on real GS-heavy content stays within ±3.5 dB worst case across the entire spectrum, with bass essentially identical (±1 dB).

## Project layout

```
MidiSharp.slnx                       Root solution file
├── src/
│   ├── MidiSharp.Core/              MIDI file parsing, sequencing, tempo map, lyric parser (netstandard2.1)
│   ├── MidiSharp.Synth/             Software synthesizer + RealtimePlayer (netstandard2.1)
│   ├── MidiSharp.Synth.OwnAudio/    Cross-platform audio backend via OwnAudioSharp (net10.0)
│   └── MidiSharp.Sf2/               Legacy SF2 library retained for the writer support SF2.Net lacks
├── Loader/                          Soundbank loading subsystem
│   ├── Loader/Loader/               Unified loader — depends on all four <format>.Net readers and (planned) MidiSharp.Core for IR; hosts the format-to-IR translators
│   ├── SF2/
│   │   ├── SF2.Net/                 SF2 reader — RIFF parsing, presets/instruments/zones, sample chunk access
│   │   ├── SF2.Net.Tests/
│   │   └── SF2.Net.SmokeTest/
│   ├── SF3/SF3.Net/                 SF3 reader (Vorbis-compressed SF2) — scaffolding, not yet implemented
│   ├── SFZ/SFZ.Net/                 SFZ reader — text + WAV references; scaffolding
│   └── DLS/DLS.Net/                 DLS Level 1/2 reader — scaffolding
├── samples/MidiSharp.Demo/          Live + render-to-WAV demo
├── tests/                           xUnit tests (currently 36 passing)
├── docs/                            Design docs for the planned IR refactor (see Roadmap below)
└── MIDI/                            Reference PDFs (MIDI 1.0 / 2.0 / SMF / RPs / Universal SysEx)
```

The `<format>.Net` projects are intentionally MidiSharp-free — pure-managed readers, publishable as independent NuGet packages. The bridge between them and the synth lives in `MidiSharp.Core` (which will own the SoundBank IR) and the unified `Loader/Loader/` project (which translates each format's parsed types into IR); see [`docs/sound-bank-loader.md`](docs/sound-bank-loader.md).

Build everything from the repo root:

```bash
dotnet build MidiSharp.slnx
dotnet test  MidiSharp.slnx
```

## What's implemented

### MIDI protocol surface

| Category | Coverage |
|---|---|
| Channel messages (0x80-0xEF) | Note On/Off, Poly Pressure, Channel Pressure, Pitch Bend, Program Change |
| CCs | 0/32 (Bank MSB/LSB), 1 (Mod), 5/65/84 (Portamento), 6/38 (Data Entry), 7 (Volume), 8 (Balance), 10 (Pan), 11 (Expression), 64-67 (Sustain/Sostenuto/Soft), 71-78 (GM2 sound controllers), 80-83 (GP), 88 (Hi-res velocity), 91-95 (Effect depths), 96/97 (Data Inc/Dec), 98/99/100/101 (NRPN/RPN), 120-127 (Channel mode) |
| RPNs | 0,0 Pitch Bend Range; 0,1 Fine Tune; 0,2 Coarse Tune; 0,3/0,4 Tuning Program/Bank; 0,5 Modulation Depth Range |
| NRPNs | GS drum family (0x18-0x1E per-key coarse/fine pitch, level, pan, reverb send, chorus send); XG channel family (0x01 + vibrato/filter/EG) |
| SysEx | GM/GM2/GS/XG Reset, Universal Master Volume/Pan/Fine/Coarse Tuning, GS Master Volume/Tune/Pan/Key Shift, GS Reverb/Chorus parameter, GS Part Parameters (Use For Rhythm, Pitch Key Shift, Volume, Pan, Velocity Sense), XG Master/Part Mode, MTS (accepted) |
| SMF Meta | SetTempo & EOT consumed by sequencer; lyrics/markers/text/copyright/signatures surfaced via `RealtimePlayer.MetaEventDispatched`; ChannelPrefix/MidiPort intentionally dropped (single-port synth) |

### SF2 engine

- All 10 SF2 default modulators (velocity → attenuation/filter, CC91/93 sends, CC1/aftertouch/poly-aftertouch → vibrato depth)
- EMU8k attenuation factor (0.4 scaling) applied to InitialAttenuation
- 7-point windowed-sinc interpolation
- Per-voice envelopes (volume + modulation), per-voice LFOs (modulation + vibrato), 2-pole resonant low-pass filter
- FDN reverb (Jot 1991, 8 lines, Hadamard feedback) and stereo chorus
- Voice retrigger semantics, exclusive class handling, sample looping with loop-until-release
- RealtimePlayer drives the synth from the audio callback for sample-accurate event timing on every platform

### Recommended Practices

Implemented: RP-001 (SMF 1.0), RP-013 (GM Level 1), RP-014 (Bank Select Response), RP-015 (Reset All Controllers), RP-018 (RPN Sensitivity), RP-019 (Device/Program Name Meta), RP-021 (Sound Controllers), RP-022 (General MIDI 2), RP-026 (Lyric/Display Extensions — via `MidiSharp.Lyrics.LyricStream`), RP-038 (GM2 Default Modulators), CA-031 (High-Resolution Velocity Prefix). RP-017 (Lyric Definition) is satisfied by dispatching events to callers verbatim.

### Verification

| Reference MIDI | Worst-band ΔdB vs fluidsynth | RMS Δ |
|---|---|---|
| Breakout (38 aftertouch, GS reverb/chorus SysEx) | ±2.5 dB | -0.85 dB |
| J-cycle (GS Reset, part-params, 517 pitch bends) | +3.97 dB | -0.46 dB |
| Jump! (post-LFO-generator fix) | -4.26 dB at 10-20 kHz; ≤2 dB elsewhere | -0.99 dB |
| Jump! with Tyroland (same-soundfont A/B) | -3.45 dB at 500-2000 Hz | -1.67 dB |

36 unit tests cover MIDI file parsing, sequencer timing, tempo map, and RP-026 lyric parsing.

## Out-of-scope by design

- **Mastering / output EQ / limiting** — the synth renders the spec; mastering belongs in the caller's signal chain.
- **MIDI 2.0 / UMP** — different wire format, per-note expression model doesn't map cleanly to SF2, no real-world content. Could revisit when real Clip Files appear.
- **XMF container format** (RP-032 through RP-037), Mobile DLS (RP-031), Scalable Polyphony (RP-027), Mobile Phone Control (RP-028).
- **GM1/GM2 sound-set name registries** (RP-024/029/039) — descriptive only; the SF2 supplies actual sounds.

## Roadmap: multi-format support via a common SoundBank IR

The plan is to add SF3, SFZ, and DLS support alongside SF2 by generalizing the synth's input. Every loader produces the same in-memory representation (`SoundBank`); the synth consumes that representation and never branches on source format. Designed for desktop and mobile — structural data resident in RAM, sample data backed by mmap with format-specific decoders.

Three design docs spell out the contract:

- [`docs/sound-bank-loader.md`](docs/sound-bank-loader.md) — loader architecture, dispatch, lifetime/threading rules, per-format sample-source strategies.
- [`docs/sound-bank-ir.md`](docs/sound-bank-ir.md) — field-by-field reference for the in-memory IR. Domain-natural units (seconds, Hz, dB), pre-flattened zones, route-as-data modulation.
- [`docs/synth-genericization.md`](docs/synth-genericization.md) — step-by-step refactor sequence for the existing SF2-shaped synth, with concrete code sketches and a regression-test strategy that keeps audio output stable at each step.

### Where each piece lives

| Component | Location | Status |
|---|---|---|
| Pure SF2 reader | `Loader/SF2/SF2.Net/` | Working |
| Pure SF3 reader | `Loader/SF3/SF3.Net/` | Scaffolding |
| Pure SFZ reader | `Loader/SFZ/SFZ.Net/` | Scaffolding |
| Pure DLS reader | `Loader/DLS/DLS.Net/` | Scaffolding |
| Unified Loader project | `Loader/Loader/` | Scaffolding (references all four readers; no translators yet) |
| SoundBank IR types | `src/MidiSharp.Core/SoundBank/` (planned) | Not started |
| Format-to-IR translators | `Loader/Loader/` (planned, alongside unified dispatch) | Not started |

### Sequencing

1. Define the IR in `MidiSharp.Core`. No synth changes; just types.
2. Add the SF2-to-IR translator and dispatch entry point inside `Loader/Loader/`. Verify round-trip rendering matches current output within ±0.5 dB.
3. Generalize the synth's `Voice` to consume IR directly, with the old SF2-direct path kept alive in parallel; switch over gradually. Full sequence in [`docs/synth-genericization.md`](docs/synth-genericization.md).
4. Add the SF3 translator inside `Loader/Loader/` (Vorbis decode in the sample source, otherwise identical to SF2 — same translator with a different sample-source implementation).
5. Add the SFZ translator (text parser + WAV resolution + opcode → IR translation).
6. Add the DLS translator (RIFF parser closer to SF2 than SFZ).
7. Memory-mapped sample sources for SF2/SF3/SFZ/DLS — orthogonal to the IR work; ship per format when the IR is ready.

Total estimated effort 4-6 weeks of focused work, none of it big-bang. The synth keeps rendering correctly at every commit.

### Other potential future work

- **UMP-to-MIDI-1.0 translation layer** if MIDI 2.0 hardware comes through the door (most "MIDI 2.0" devices ship in 1.0-compatibility mode anyway).
- **Master EQ / mastering** as a *separate* helper project. The synth deliberately renders the spec without master processing.

## License

MIT — see [LICENSE](LICENSE) for the full text.
