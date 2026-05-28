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
MidiSharp.slnx                    Root solution file
├── src/
│   ├── MidiSharp.Core/           MIDI file parsing, sequencing, tempo map, lyric parser (netstandard2.1)
│   ├── MidiSharp.Synth/          Software synthesizer + RealtimePlayer (netstandard2.1)
│   ├── MidiSharp.Synth.OwnAudio/ Cross-platform audio backend via OwnAudioSharp (net10.0)
│   └── MidiSharp.Sf2/            Legacy SF2 reader, retained for the writer support SF2Net lacks
├── SF2Net/
│   └── SF2Net/                   Pure-managed SF2 reader (netstandard2.1)
├── samples/MidiSharp.Demo/       Live + render-to-WAV demo
├── tests/                        xUnit tests (currently 36 passing)
└── MIDI/                         Reference PDFs (MIDI 1.0 / 2.0 / SMF / RPs / Universal SysEx)
```

Build everything from the `MidiSharp/` directory:

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

## Roadmap: SFZ support via parallel SFZNet project

The plan is to add SFZ format support as a sibling reader project (`SFZNet`) alongside the existing `SF2Net`, letting the synth consume either format without changing the audio code.

### Phase 1 — SFZ → SF2 translation layer (planned first cut)

`SFZNet` parses SFZ text files and exposes the *same* types `SF2Net` does (`SoundFont`, `Preset`, `Instrument`, `Zone`, `Generator`, `SampleHeader`). The synth doesn't know which backend loaded it. This covers ~80-90% of real-world SFZ files with zero synth changes.

**SFZ opcode → SF2 generator mapping (initial set):**

| SFZ opcode | SF2 generator | Notes |
|---|---|---|
| `sample=` | (sample reference) | Resolve relative paths via `<control> default_path=`; decode all referenced WAVs into one combined float buffer at load time |
| `lokey` / `hikey` | `KeyRange` | |
| `lovel` / `hivel` | `VelRange` | |
| `pitch_keycenter` | `OverridingRootKey` | |
| `tune` / `transpose` | `FineTune` / `CoarseTune` | |
| `volume` | `InitialAttenuation` | dB → cB, negate |
| `pan` | `Pan` | -100..+100 → -500..+500 |
| `ampeg_attack/decay/sustain/release` | `AttackVolEnv` / `DecayVolEnv` / `SustainVolEnv` / `ReleaseVolEnv` | Seconds → timecents, sustain % → cB |
| `fileg_*` | `*ModEnv` | Modulation envelope |
| `cutoff` | `InitialFilterFc` | Hz → absolute cents |
| `resonance` | `InitialFilterQ` | dB → cB |
| `lfoN_freq` / `_delay` | `FreqVibLFO` / `DelayVibLFO` | Hz → absolute cents; sec → timecents |
| `loop_mode=loop_continuous` | `SampleModes=1` | |
| `loop_mode=loop_sustain` | `SampleModes=3` | |
| `loop_start` / `loop_end` | Sample header `StartLoop` / `EndLoop` | After WAV decode |
| `group=` (exclusive group) | `ExclusiveClass` | |
| `<global>` / `<group>` / `<region>` | preset → instrument → zone hierarchy | SFZ uses straight override; map carefully to SF2's absolute-set vs additive-offset distinction |

### Phase 2 — opcodes that need new infrastructure

Things SFZ supports that SF2 doesn't model directly. Defer to Phase 2:

- **Round-robin / sequence**: `seq_position`, `seq_length` — needs a per-region sequence counter on the synth side.
- **Keyswitch**: `sw_lokey` / `sw_hikey` / `sw_default` / `sw_last` — region activated/deactivated by a held key.
- **CC-conditional regions**: `locc<n>` / `hicc<n>` — only play if CC value in range. Maps loosely to SF2 modulator source but with extra gating logic.
- **Crossfades**: `xfin_lovel` / `xfin_hivel` / `xfout_lovel` / `xfout_hivel` — velocity/key/CC crossfade between layered regions.
- **Polyphony groups with `off_by`**: per-`group=` polyphony cap with stealing rules.

These would either extend the SF2 generator enum with SFZ-specific opcodes that `Voice` understands when present, or motivate a move to Phase 3.

### Phase 3 — common abstract model (only if needed)

If Phase 2 patches start feeling kludgy, introduce a backend-neutral `MidiSharp.SoundFontModel` and have both `SF2Net` and `SFZNet` produce it. Voice/Synthesizer talk only to the common model. This is a real refactor (touches every `voice.Configure`, generator iteration, sample-data access) but cleanly separates "loader" from "renderer". Not started until/unless concrete pain demands it.

### Other potential future work

- **DLS Level 1/2 soundfonts** as a third sibling reader. Closer to SF2 in design than SFZ is.
- **SF3 support** (SF2 with Vorbis-compressed samples) — small extension on top of SF2Net.
- **UMP-to-MIDI-1.0 translation layer** if MIDI 2.0 hardware comes through the door.
- **Master EQ / mastering** in a *separate* helper project, not in the synth itself.

## License

MIT — see [LICENSE](LICENSE) for the full text.
