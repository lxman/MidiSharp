# MidiSharp

A cross-platform, pure-managed C# software synthesizer that renders MIDI files through SoundFont presets — **SF2, SF3, SFZ, and DLS**, all loaded into one common in-memory representation (the *SoundBank IR*). Built around a fully spec-compliant MIDI 1.0 / GM / GM2 / GS / XG protocol implementation and an SF2-style generator/modulator engine validated against fluidsynth.

It also ships a patch-level **instrument-substitution** layer: list the instruments a song calls for and swap any of them for an instrument cherry-picked from another font, without altering the song's sequencing.

## Quick start

```bash
# Live playback (use single quotes around files with ! or spaces)
dotnet run --project samples/MidiSharp.Demo -- '<song>.mid' '<base>.sf2'
# Controls: Space pause/resume, S reset, Q quit

# Render to WAV instead of live playback
dotnet run --project samples/MidiSharp.Demo -- '<song>.mid' '<base>.sf2' /tmp/out.wav

# List the patches (instruments) a song uses, named against a base font
dotnet run --project samples/MidiSharp.Demo -- --patches '<song>.mid' '<base>.sf2'

# Override patches with instruments from other fonts (repeatable), then play or render
dotnet run --project samples/MidiSharp.Demo -- '<song>.mid' '<base>.sf2' \
  --map 30=OtherGM.sf2            # program 30 ← OtherGM's program 30 (GM-aligned)
  # --map 30=Guitars.sf2:5       program 30 ← Guitars' program 5
  # --map 128:0=OtherDrums.sf2   swap the whole drum kit (bank 128)
```

A browser-based player is also included:

```bash
dotnet run --project samples/MidiSharp.Server -- --sf-root ~/soundfonts --midi-root ~/midi
# then open http://localhost:5005 — a file browser for picking the song and fonts,
# a "patches used" panel, and per-patch override pickers. Audio plays on the server machine.
```

Tested daily against `GeneralUser-GS.sf2` and `TyrolandSFX.sf2`. End-to-end A/B vs fluidsynth on real GS-heavy content stays within ±3.5 dB worst case across the entire spectrum, with bass essentially identical (±1 dB).

## Project layout

```
MidiSharp.slnx                       Root solution file
├── src/
│   ├── MidiSharp.Core/              MIDI parsing, sequencing, tempo map, lyrics, AND the SoundBank IR (netstandard2.1)
│   ├── MidiSharp.Audio/             Sample-file decoders — WAV/AIFF/FLAC/Vorbis/PCM (netstandard2.1)
│   ├── MidiSharp.Synth/             Software synthesizer + RealtimePlayer; consumes the IR (netstandard2.1)
│   ├── MidiSharp.Synth.OwnAudio/    Cross-platform audio output via OwnAudioSharp (net10.0)
│   └── MidiSharp.PatchMap/          Instrument substitution — composes SoundBanks (netstandard2.1, Core-only)
├── Loader/                          Soundbank loading → SoundBank IR
│   ├── Loader/Loader/               Unified loader + format-to-IR translators (SF2/SF3/SFZ/DLS) + sample sources
│   ├── SF2/SF2.Net/                 SF2 reader — RIFF parsing, presets/instruments/zones, sample access (+ tests)
│   ├── SF3/SF3.Net/                 SF3 reader (Vorbis-compressed SF2)
│   ├── SFZ/SFZ.Net/                 SFZ reader (text opcodes + WAV references)
│   └── DLS/DLS.Net/                 DLS Level 1/2 reader
├── samples/
│   ├── MidiSharp.Demo/              CLI: live playback / WAV render / --patches / --map
│   └── MidiSharp.Server/            Browser player: file browser + per-patch override UI
├── tests/                           xUnit — 101 passing (Core, Synth, Loader, Audio, SF2.Net, PatchMap)
├── docs/                            SoundBank IR + loader design docs
└── MIDI/                            Reference PDFs (MIDI 1.0 / 2.0 / SMF / RPs / Universal SysEx)
```

The `<format>.Net` reader projects are intentionally MidiSharp-free — pure-managed, publishable as independent NuGet packages. The bridge between them and the synth is `MidiSharp.Core` (which owns the SoundBank IR) and the unified `Loader/Loader/` project (which translates each format's parsed types into IR); see [`docs/sound-bank-loader.md`](docs/sound-bank-loader.md).

Build and test everything from the repo root:

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

### Synthesis engine

- All 10 SF2 default modulators (velocity → attenuation/filter, CC91/93 sends, CC1/aftertouch/poly-aftertouch → vibrato depth)
- EMU8k attenuation factor (0.4 scaling) applied to InitialAttenuation
- 7-point windowed-sinc interpolation
- Per-voice envelopes (volume + modulation), per-voice LFOs (modulation + vibrato), 2-pole resonant low-pass filter
- FDN reverb (Jot 1991, 8 lines, Hadamard feedback) and stereo chorus
- Voice retrigger semantics, exclusive class handling, sample looping with loop-until-release
- The engine consumes the **format-neutral SoundBank IR** and never branches on source format; `RealtimePlayer` drives it from the audio callback for sample-accurate event timing on every platform

### Multi-format loading (SoundBank IR)

Every supported format loads through one in-memory representation. `SoundBankLoader.Load(path)` sniffs the format and dispatches to a translator that flattens the source hierarchy into the IR (`SoundBank` → `Patch` → `PatchZone` → `SampleRef`); the synth resolves every note through a single seam, `SoundBank.FindPatch(bank, program)`, and reads samples through one `ISampleSource`.

| Format | Reader | Status |
|---|---|---|
| SF2 | `Loader/SF2/SF2.Net/` | Working — the primary, fluidsynth-validated path; memory-mapped sample source |
| SF3 | `Loader/SF3/SF3.Net/` | Implemented — lazy Vorbis-decoding sample source |
| SFZ | `Loader/SFZ/SFZ.Net/` | Implemented — text-opcode parser + WAV resolution; supports keyswitch, round-robin, CC gates |
| DLS | `Loader/DLS/DLS.Net/` | Implemented — Level 1/2 RIFF + articulation translation |

SF2 is the most thoroughly validated; SF3/SFZ/DLS are newer but render through the same IR and synth path. The contract is documented in [`docs/sound-bank-ir.md`](docs/sound-bank-ir.md) and [`docs/sound-bank-loader.md`](docs/sound-bank-loader.md).

### Recommended Practices

Implemented: RP-001 (SMF 1.0), RP-013 (GM Level 1), RP-014 (Bank Select Response), RP-015 (Reset All Controllers), RP-018 (RPN Sensitivity), RP-019 (Device/Program Name Meta), RP-021 (Sound Controllers), RP-022 (General MIDI 2), RP-026 (Lyric/Display Extensions — via `MidiSharp.Lyrics.LyricStream`), RP-038 (GM2 Default Modulators), CA-031 (High-Resolution Velocity Prefix). RP-017 (Lyric Definition) is satisfied by dispatching events to callers verbatim.

### Verification

| Reference MIDI | Worst-band ΔdB vs fluidsynth | RMS Δ |
|---|---|---|
| Breakout (38 aftertouch, GS reverb/chorus SysEx) | ±2.5 dB | -0.85 dB |
| J-cycle (GS Reset, part-params, 517 pitch bends) | +3.97 dB | -0.46 dB |
| Jump! (post-LFO-generator fix) | -4.26 dB at 10-20 kHz; ≤2 dB elsewhere | -0.99 dB |
| Jump! with Tyroland (same-soundfont A/B) | -3.45 dB at 500-2000 Hz | -1.67 dB |

101 unit tests across the suite cover MIDI parsing, sequencer timing, tempo map, RP-026 lyric parsing, the SF2 reader, synthesis, the loaders, and patch substitution.

## Instrument substitution (`MidiSharp.PatchMap`)

A standalone module that lets you re-author what a song plays without touching the song. The mental model: a soundfont *is* a `(bank, program) → instrument` map, and the synth resolves every note through exactly one seam (`SoundBank.FindPatch`). So substitution is a SoundBank-**composition** problem, not a synth change.

- **`PatchUsageAnalyzer`** — walks the sequenced events and reports the patches a song actually uses (the resolved `(bank, program)` active at each note), named against a base font. Bank resolution is shared with the synth (`MidiSharp.SoundBank.BankResolution`) so the list matches playback exactly.
- **`PatchMapSession`** — holds a base font, any number of preloaded *source* fonts, and a map of overrides: `(logical bank, program) → a patch in a source font`. `BuildComposite()` returns a `SoundBank`.
- **`SoundBankComposer` + `ConcatenatedSampleSource`** — the composite is base patches ∪ overrides over a concatenated sample space (overridden zones get their `SampleId` re-based; stereo links rebased). The synth consumes it through the ordinary `LoadSoundFont`/`FindPatch` contract and can't tell it from a native font — **zero synth changes.**

Sequencing is untouched: the file's program changes still pick *logical* patches; only what they resolve to changes. Overrides are sparse by default (a few on top of a base font) or can cover the whole set; drums are swapped as a whole kit (bank 128). The session owns the lifetime of the base + source fonts; composites are lightweight borrowed views, rebuilt per playback.

Both front-ends use it: the Demo CLI (`--patches`, `--map`) and the web player (file browser + per-patch override pickers, building the composite server-side before play).

## Out-of-scope by design

- **Mastering / output EQ / limiting** — the synth renders the spec; mastering belongs in the caller's signal chain (or a separate helper project).
- **Hardware / OS MIDI-port output** — this renders *audio* from MIDI files; it is not a MIDI router. `RealtimePlayer` drives the in-process synth directly. (The legacy WinMM/CoreMIDI `IMidiOutput` + `MidiPlayer` path has been removed.)
- **SoundFont authoring / writing** — load-and-render only; the standalone SF2 read/write library has been removed. Recreate it if editing is ever needed.
- **MIDI 2.0 / UMP** — different wire format; the per-note expression model doesn't map cleanly to SF2's channel-sourced modulators, and there's no real-world content yet. Could revisit when real Clip Files appear (MPE is the lower-cost on-ramp if per-note expression is ever wanted).
- **XMF container format** (RP-032 through RP-037), Mobile DLS (RP-031), Scalable Polyphony (RP-027), Mobile Phone Control (RP-028).
- **GM1/GM2 sound-set name registries** (RP-024/029/039) — descriptive only; the soundfont supplies the actual sounds.

## Future work

- **Memory-mapped/streaming sample sources for SFZ and DLS** (SF2 is mmap'd, SF3 lazily decodes Vorbis).
- **Global "search all fonts by name"** in the web player's file browser (it currently filters per folder).
- **Saved override presets** ("remaps") and instrument **upload** in the web player (overrides are currently in-session, sourced from the configured font folders).
- **UMP-to-MIDI-1.0 translation layer** if MIDI 2.0 hardware ever shows up (most "MIDI 2.0" devices ship in 1.0-compatibility mode anyway).
- Further SF3/SFZ/DLS hardening toward the SF2 path's fluidsynth-validated fidelity.

## License

MIT — see [LICENSE](LICENSE) for the full text.
