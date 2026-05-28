# SoundBank IR — Field Reference

The intermediate representation produced by every loader and consumed by the synth. This doc is the field-by-field shape; the architectural context is in [`sound-bank-loader.md`](sound-bank-loader.md).

All types live in `MidiSharp.Core` under `src/MidiSharp.Core/SoundBank/`. The format-to-IR translators (`Sf2BankLoader`, `Sf3BankLoader`, `SfzBankLoader`, `DlsBankLoader`) all live inside the single unified `Loader/Loader/` project, organized into per-format subfolders. They consume only these IR types when producing their output — none of them define IR types of their own.

## Design principles

These are non-negotiable rules the IR enforces, called out so future field additions stay consistent:

1. **Domain-natural units only.** Seconds, Hz, dB, semitones, cents, normalized 0..1, normalized -1..+1. No timecents, no centibels, no absolute-cents-from-8.176-Hz. SF2's encoding peculiarities are the loader's problem, not the synth's.
2. **Pre-flattened zones.** No inheritance, no global/group/preset cascades. Each `PatchZone` is fully self-contained, ready to drive one voice. Loaders merge their format's hierarchy once at load time.
3. **Immutable after construction.** Every field is `init`-only. The IR is safe to share across threads without locks.
4. **Optional features are nullable.** `KeySwitch?`, `RoundRobin?`, `Filter?`, `ModulationEnvelope?`, `VibratoLFO?` — `null` means "this feature doesn't apply to this zone." The synth checks once per NoteOn; zero overhead when absent.
5. **No format-specific names.** Field names describe what they do (`CutoffHz`), not where they came from (`InitialFilterFc`). The IR is the lingua franca; nobody should be able to tell which loader produced a given `SoundBank` by reading field names.

## Top-level types

### `SoundBank`

```csharp
public sealed class SoundBank : IDisposable
{
    public string Name { get; init; }
    public string? Author { get; init; }
    public string? Copyright { get; init; }
    public string? Comment { get; init; }
    public SoundBankFormat SourceFormat { get; init; }
    public IReadOnlyList<Patch> Patches { get; init; }
    public ISampleSource Samples { get; init; }

    public Patch? FindPatch(int bank, int program);
    public void Dispose();
}
```

`Name` is required; everything else metadata is `string?`. `FindPatch` is the synth's NoteOn entry point — must be O(1) or O(log n). Implementation detail: prebuild a `Dictionary<(int bank, int program), Patch>` at load time.

### `Patch`

```csharp
public sealed class Patch
{
    public int Bank { get; init; }            // 0-128 typical; 128 = GM drum bank
    public int Program { get; init; }         // 0-127
    public string Name { get; init; }
    public IReadOnlyList<PatchZone> Zones { get; init; }
}
```

A patch is whatever a Bank Select + Program Change resolves to. It owns a flat list of `PatchZone`s; ordering matters only for round-robin and for legacy "earliest-matching-zone wins" SF2 behavior (which the synth doesn't actually need — every matching zone plays as a separate voice layer).

## `PatchZone` — the workhorse

This is the meat of the IR. One zone = one voice when matched by a NoteOn.

```csharp
public sealed class PatchZone
{
    // ─── Activation conditions ──────────────────────────────────────
    public KeyRange Keys { get; init; }
    public VelocityRange Velocities { get; init; }
    public IReadOnlyList<CCGate> CCGates { get; init; }   // empty for SF2/SF3/DLS
    public KeySwitch? KeySwitch { get; init; }            // SFZ sw_*; null elsewhere
    public RoundRobin? RoundRobin { get; init; }          // SFZ seq_*; null elsewhere
    public int? ExclusiveGroup { get; init; }             // SF2 ExclusiveClass, SFZ group= + off_by=

    // ─── Sample reference ───────────────────────────────────────────
    public SampleRef Sample { get; init; }

    // ─── Static playback parameters ─────────────────────────────────
    public PitchSettings Pitch { get; init; }
    public LevelSettings Level { get; init; }

    // ─── Time-varying modulators ────────────────────────────────────
    public EnvelopeSettings VolumeEnvelope { get; init; }   // required; always present
    public EnvelopeSettings? ModulationEnvelope { get; init; }
    public LFOSettings? VibratoLFO { get; init; }
    public LFOSettings? ModulationLFO { get; init; }
    public FilterSettings? Filter { get; init; }

    // ─── Sends ──────────────────────────────────────────────────────
    public double ReverbSend { get; init; }                // 0..1
    public double ChorusSend { get; init; }                // 0..1

    // ─── Routing matrix ─────────────────────────────────────────────
    public IReadOnlyList<ModulationRoute> Routes { get; init; }
}
```

The synth's voice setup is: validate the activation conditions; if they pass, allocate a voice and copy/initialize from the static and modulator fields; subscribe the voice to the routes.

### Activation conditions

#### `KeyRange` / `VelocityRange`

```csharp
public readonly record struct KeyRange(byte Low, byte High);
public readonly record struct VelocityRange(byte Low, byte High);
```

Inclusive on both ends. Default-construct values are `(0, 127)` — "all keys" / "all velocities." Always present (never null); the loader fills them in even when the source format doesn't specify.

#### `CCGate`

```csharp
public readonly record struct CCGate(byte Controller, byte Low, byte High);
```

SFZ `locc<n>` / `hicc<n>`. A zone is gated by **all** CCs in its `CCGates` list — i.e., AND semantics. Empty list = "no CC gating." SF2/SF3/DLS loaders always produce an empty list; only SFZ populates this.

#### `KeySwitch`

```csharp
public readonly record struct KeySwitch(byte Low, byte High, byte Default, byte? LastPressed);
```

SFZ-style keyswitching: keys in `[Low, High]` aren't notes — pressing one selects an articulation. `Default` is the articulation active before any keyswitch has been pressed. The synth tracks `LastPressed` per channel; a zone is active iff its `KeySwitch.LastPressed == thisZonesActivationKey`.

The loader emits `KeySwitch` as a property of every zone in the keyswitch group, with the activation key encoded in the zone's `Sample.Name` or a separate field. Detailed encoding TBD when SFZ loader lands.

#### `RoundRobin`

```csharp
public readonly record struct RoundRobin(int Position, int Length);
```

SFZ `seq_position=N seq_length=M`: this zone plays on the N-th of every M consecutive matching NoteOns. The synth keeps a per-patch counter, modulo `Length`. Position is 1-indexed by SFZ convention; the synth converts to 0-indexed at load time.

#### `ExclusiveGroup`

```csharp
public int? ExclusiveGroup { get; init; }
```

SF2 `ExclusiveClass` and SFZ `group=` + `off_by=`: when a NoteOn allocates a voice in group N, any currently-sounding voices in the same group on the same channel are silenced. Null means "no exclusive grouping" (most zones).

### Sample reference

```csharp
public sealed class SampleRef
{
    public int SampleId { get; init; }                       // index into SoundBank.Samples
    public LoopMode LoopMode { get; init; }
    public int? OverridingRootKey { get; init; }             // null = use sample's RootKey
    public double FineTuneCents { get; init; }
    public double CoarseTuneSemitones { get; init; }
    public double ScaleTuningCentsPerKey { get; init; } = 100.0;

    // Sample addressing offsets (advanced; usually default)
    public long? StartOffset { get; init; }                  // start frame, sample-relative
    public long? EndOffset { get; init; }                    // end frame, sample-relative
    public long? LoopStartOffset { get; init; }              // override metadata's LoopStart
    public long? LoopEndOffset { get; init; }                // override metadata's LoopEnd
}

public enum LoopMode { None, Continuous, UntilRelease }
```

The `SampleId` indexes into `SoundBank.Samples.Metadata(id)` for rate/length/loops. `OverridingRootKey` / fine / coarse tune fold into the playback pitch calculation.

`StartOffset` / `EndOffset` / `LoopStart/EndOffset` exist for SF2's "sample offset" generators that let a zone use only part of a larger sample. Almost always null; when set, they're sample-relative frame indices (not the SF2 absolute-byte offsets — the loader translates).

### Pitch & level

```csharp
public sealed class PitchSettings
{
    public double FineTuneCents { get; init; }       // -100..+100
    public double CoarseTuneSemitones { get; init; } // -120..+120
}

public sealed class LevelSettings
{
    public double AttenuationDb { get; init; }       // 0 = unity gain; positive = quieter
    public double Pan { get; init; }                 // -1.0 = full left, 0 = center, +1.0 = full right
}
```

Pitch settings combine with `SampleRef.FineTuneCents` / `CoarseTuneSemitones` at voice setup. Separation is historical (SF2 has both `PitchSettings`-style zone-wide tuning and per-sample tuning on the sample header); having both fields available means the loader can preserve the distinction.

`AttenuationDb` is non-negative by convention (synthesis only attenuates samples, never boosts them — boosting clips). The SF2 EMU 0.4 factor is applied by the loader, not the synth.

### Envelopes

```csharp
public sealed class EnvelopeSettings
{
    public double DelaySeconds { get; init; }            // before attack starts
    public double AttackSeconds { get; init; }           // 0 → peak (linear in dB)
    public double HoldSeconds { get; init; }             // at peak
    public double DecaySeconds { get; init; }            // peak → sustain (linear in dB)
    public double SustainLevel { get; init; }            // 0..1 normalized
    public double ReleaseSeconds { get; init; }          // sustain → 0 (linear in dB)
    public double KeynumToHoldCentsPerKey { get; init; } // SF2 modifier; key-dependent hold
    public double KeynumToDecayCentsPerKey { get; init; }// SF2 modifier; key-dependent decay
}
```

Volume envelope is **always present** on every zone (`PatchZone.VolumeEnvelope` is non-nullable). Modulation envelope is **optional** (`PatchZone.ModulationEnvelope` is nullable) — SF2 always emits one, SFZ emits one if any `fileg_*` opcodes appear, DLS Level 1 doesn't have one at all.

Time values are seconds in the time domain — SF2's timecent encoding (`2^(t/1200)` seconds where `t` is the SF2 short value) is converted at load. `0.0` means "instantaneous" (no delay/attack/hold/decay/release stage), and the synth skips that phase.

`SustainLevel` is `0..1` linear amplitude for the volume envelope and `0..1` linear amount for the modulation envelope (whose destination depends on the routes). SF2's "sustain is in centibels of attenuation below peak" encoding is converted at load.

`KeynumToHold` and `KeynumToDecay` are per-key scaling: at each MIDI key away from key 60 (middle C), the hold/decay phase length is offset by N cents (where 1200 cents = 2× time). Used by sample libraries to make high notes decay faster than low notes naturally. Default 0; non-zero means the synth scales the times at NoteOn based on the key number.

### LFOs

```csharp
public sealed class LFOSettings
{
    public double DelaySeconds { get; init; }      // before LFO starts oscillating
    public double FrequencyHz { get; init; }       // typically 0.1-20 Hz
    public double PitchDepthCents { get; init; }   // 0 = no pitch mod; positive = LFO modulates pitch
    public double VolumeDepthDb { get; init; }     // tremolo; 0 = no volume mod
    public double FilterDepthCents { get; init; }  // filter sweep; 0 = no filter mod
}
```

Two LFOs are independently optional: `VibratoLFO` (traditionally pitch-only in SF2, hence the name) and `ModulationLFO` (traditionally for volume tremolo and filter sweep). The field names reflect SF2's convention but the IR doesn't enforce it — a `VibratoLFO` with non-zero `VolumeDepthDb` is well-defined and the synth will apply it.

Frequency in Hz, not absolute cents. Depth is signed (positive = LFO peaks raise pitch / volume / cutoff; negative = peaks lower them).

### Filter

```csharp
public sealed class FilterSettings
{
    public FilterType Type { get; init; }
    public double CutoffHz { get; init; }
    public double ResonanceDb { get; init; }              // 0 = no resonance; typical max ~24
    public double KeyTrackCentsPerKey { get; init; }      // 0 = no keytrack; 100 = full keytrack
    public double VelocityToCutoffCents { get; init; }    // SF2 default modulator; -2400 typical
    public double EnvelopeDepthCents { get; init; }       // mod envelope → cutoff
    public double LfoDepthCents { get; init; }            // mod LFO → cutoff
}

public enum FilterType { LowPass, HighPass, BandPass, LowShelf, HighShelf, Notch }
```

`PatchZone.Filter` is nullable. Null = no filter, sample passes through. Non-null = filter active with the given type and modulation routing.

SF2 only specifies `LowPass`. SFZ adds the others. DLS Level 2 has low-pass. Loaders set `Type = LowPass` when the source format doesn't differentiate.

`KeyTrackCentsPerKey` controls how cutoff scales with played key — at `100`, cutoff rises one octave per octave of key (full tracking, like an analog filter following pitch). Default `0`, no tracking.

The dedicated `VelocityToCutoffCents` exists because SF2's default modulator #2 (velocity → filter cutoff at amount -2400) is so universally applied that having it as a direct field on `FilterSettings` saves the synth from looking it up in `Routes` on every NoteOn. The loader still emits it as a route if the source specifies it; the synth treats this field as a fast path for the common case.

### Sends

```csharp
public double ReverbSend { get; init; }    // 0..1
public double ChorusSend { get; init; }    // 0..1
```

Per-zone effect send levels. The synth's reverb and chorus busses receive the sum of `voice_output × send_level` across all voices. Channel-level CC91/CC93 contributions are added at process time by the synth (not in the IR — they're per-channel MIDI state, not per-zone configuration).

`0.0` is "fully dry, no send." `1.0` is "send at unity gain to the bus." SF2's 0.1%-units encoding (where 1000 = 100%) is converted at load.

## Modulation routes — the design that pays for itself

This is the part most worth getting right. The naive approach is hard-coding the 10 SF2 default modulators as synth behavior. The IR-friendly approach is to express **all** modulation routing as data:

```csharp
public sealed class ModulationRoute
{
    public ModSource Source { get; init; }
    public ModDestination Dest { get; init; }
    public double Amount { get; init; }              // signed; units determined by Dest
    public ModTransform Transform { get; init; }
    public ModSource? AmountModulator { get; init; } // null = static amount
}
```

### Sources

```csharp
public abstract record ModSource
{
    public sealed record Velocity : ModSource;
    public sealed record KeyNumber : ModSource;
    public sealed record ChannelPressure : ModSource;
    public sealed record PolyPressure : ModSource;
    public sealed record PitchBend : ModSource;
    public sealed record ChannelController(byte Number) : ModSource;  // any CC, 0-127
    public sealed record RpnValue(byte Msb, byte Lsb) : ModSource;
    public sealed record NoConnection : ModSource;                    // SF2 zero-source-id case
}
```

Records-with-payload (`ChannelController(1)` for mod wheel, `(11)` for expression, etc.) instead of an enum-per-CC keeps the model open without growing a 128-entry enum. The synth pattern-matches in its per-block route evaluator.

### Destinations

```csharp
public enum ModDestination
{
    PitchCents,                  // ±cents added to playback pitch
    FilterCutoffCents,           // ±cents added to filter Fc
    FilterResonanceDb,           // ±dB added to filter Q
    AttenuationDb,               // ±dB added to voice attenuation (negative = louder)
    PanNormalized,               // ±value added to pan (-1..+1)
    VibratoLfoPitchDepthCents,   // adds to VibratoLFO.PitchDepthCents
    ModulationLfoPitchDepthCents,
    ModulationLfoVolumeDepthDb,
    ModulationLfoFilterDepthCents,
    ModulationEnvelopeToFilterCents,
    ModulationEnvelopeToPitchCents,
    ReverbSendAmount,            // adds to ReverbSend (clamped 0..1 after sum)
    ChorusSendAmount,
}
```

Destinations are simpler than sources — the SF2 generator vocabulary plus a few SFZ additions covers all four formats. The unit suffix (`Cents`, `Db`, `Normalized`) is part of the name so `Amount` is self-documenting.

### Transforms

```csharp
public enum ModTransform
{
    Linear,                  // y = x
    ConcaveUnipolar,         // SF2 fig E; quick rise, slow approach to 1 (~log-shaped)
    ConvexUnipolar,          // SF2 fig F; slow rise, quick approach to 1
    Switch,                  // SF2 fig G; 0 below 0.5, 1 above
    LinearBipolar,           // -1..+1
    ConcaveUnipolarNegative, // ConcaveUnipolar but inverted: velocity → attenuation curve
    // ... add as needed
}
```

SF2's transform set (10 curves total) is the canonical reference; SFZ has implicit "linear" everywhere and curve-shaping via `amp_velcurve_N` opcodes the loader can decompose. DLS articulators specify curves explicitly.

### Why records + enums and not "just amount on a property"

The alternative is having a `VelocityToAttenuationDb` field on `LevelSettings`, a `Cc1ToVibratoCents` field on `LFOSettings`, and so on. This works for the SF2 default modulators (because they're a fixed list) but breaks down the moment a custom routing appears (SF2 lets patches define arbitrary modulators; SFZ has `cutoff_cc<n>` for every CC). The route-as-data approach handles both uniformly:

- SF2 default modulator #1 (velocity → InitialAttenuation, concave, amount 960 cB): one route, `Source=Velocity, Dest=AttenuationDb, Amount=96.0, Transform=ConcaveUnipolarNegative`.
- SFZ `cutoff_cc1=2400`: one route, `Source=ChannelController(1), Dest=FilterCutoffCents, Amount=2400, Transform=Linear`.
- A patch's custom modulator routing key pressure to filter resonance: one route, no special-case in the loader or synth.

The synth iterates `zone.Routes` once per NoteOn (and per per-block update for live CC changes), looks up the current source value, applies the transform, scales by `Amount`, and adds to the destination's running total. That's the entire modulation engine.

## Default values & "field absent" semantics

Every field has a documented default so loaders can leave fields unset when the source format doesn't speak to them:

| Field | Default | Means |
|---|---|---|
| `Keys` | `(0, 127)` | All keys |
| `Velocities` | `(0, 127)` | All velocities |
| `CCGates` | empty list | No CC gating |
| `KeySwitch` | `null` | No keyswitch |
| `RoundRobin` | `null` | No round-robin |
| `ExclusiveGroup` | `null` | No exclusive grouping |
| `Pitch.FineTuneCents` | `0.0` | No fine tune |
| `Pitch.CoarseTuneSemitones` | `0.0` | No coarse tune |
| `Level.AttenuationDb` | `0.0` | Unity gain |
| `Level.Pan` | `0.0` | Center |
| `VolumeEnvelope` | All zeros, `SustainLevel=1.0` | Instant attack, no decay/release |
| `ModulationEnvelope` | `null` | No mod envelope |
| `VibratoLFO` / `ModulationLFO` | `null` | LFO inactive |
| `Filter` | `null` | No filtering |
| `ReverbSend` / `ChorusSend` | `0.0` | No effect send |
| `Routes` | empty list | No modulation routing |

A `null` LFO is genuinely faster than a zero-depth LFO — the synth's per-sample loop has nothing to do when `VibratoLFO == null`, vs. computing a sin wave whose output it then multiplies by zero. Worth the nullable.

## Sample metadata

This is what `ISampleSource.Metadata(int sampleId)` returns. RAM-resident, immutable, cheap to query:

```csharp
public sealed class SampleMetadata
{
    public string? Name { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }                    // 1 for SF2/DLS, 1 or 2 for SFZ
    public long LengthFrames { get; init; }
    public long LoopStartFrames { get; init; }
    public long LoopEndFrames { get; init; }
    public int RootKey { get; init; }                     // 0-127; 255 = unpitched
    public double PitchCorrectionCents { get; init; }     // sample's own detune
    public int? StereoLinkSampleId { get; init; }         // for L/R stereo pairs
}
```

All frame counts are sample-relative — frame 0 is the first frame of *this* sample, not byte 0 of some shared chunk. Loaders translate as needed.

`Channels = 2` means interleaved L/R inside one frame: `samples[0..1]` = first stereo frame. SF2 and DLS are mono-only at the sample level (stereo is achieved by paired mono samples with `StereoLinkSampleId` cross-references); SFZ can have native stereo WAVs. The synth handles both — it reads `Channels` and emits stereo output either by reading the linked sample or by deinterleaving.

`RootKey = 255` is the convention for unpitched samples (drum hits). The synth treats them as if `RootKey = 60` for pitch calc, but typically the drum zone overrides `Sample.OverridingRootKey` anyway.

## Worked examples

### A trivial SF2 piano zone

After the SF2 loader runs:

```csharp
new PatchZone {
    Keys = new(36, 96),
    Velocities = new(0, 127),
    Sample = new SampleRef {
        SampleId = 142,
        LoopMode = LoopMode.Continuous,
        OverridingRootKey = null,           // use sample's RootKey
        FineTuneCents = 0,
        CoarseTuneSemitones = 0,
    },
    Pitch = new() { FineTuneCents = 0, CoarseTuneSemitones = 0 },
    Level = new() { AttenuationDb = 4.0, Pan = -0.2 },
    VolumeEnvelope = new() {
        DelaySeconds = 0,
        AttackSeconds = 0.001,
        HoldSeconds = 0,
        DecaySeconds = 0.5,
        SustainLevel = 0.8,
        ReleaseSeconds = 0.3,
    },
    ModulationEnvelope = null,
    VibratoLFO = null,
    ModulationLFO = null,
    Filter = new() {
        Type = FilterType.LowPass,
        CutoffHz = 18_000,
        ResonanceDb = 0,
        VelocityToCutoffCents = -2400,      // SF2 default modulator #2
        EnvelopeDepthCents = 0,
        LfoDepthCents = 0,
    },
    ReverbSend = 0.2,
    ChorusSend = 0,
    Routes = new[] {
        new ModulationRoute {
            Source = new ModSource.Velocity(),
            Dest = ModDestination.AttenuationDb,
            Amount = 96.0,
            Transform = ModTransform.ConcaveUnipolarNegative,
        },
        // ... other SF2 default modulators
    },
}
```

### An SFZ region with keyswitching + round-robin

After the SFZ loader runs on a region with `sw_lokey=24 sw_hikey=28 sw_default=24 seq_position=1 seq_length=3`:

```csharp
new PatchZone {
    Keys = new(36, 96),
    Velocities = new(0, 127),
    KeySwitch = new(Low: 24, High: 28, Default: 24, LastPressed: 24),
    RoundRobin = new(Position: 0, Length: 3),    // 1-indexed → 0-indexed
    CCGates = Array.Empty<CCGate>(),
    // ... everything else as normal
}
```

The synth's NoteOn check: `Keys.Contains(60) && Velocities.Contains(100) && KeySwitch?.LastPressed == 24 && RoundRobin.Position == (channelState.SequenceCounter[patch] % 3)`. Each condition is a few cycles; total overhead negligible.

## What's intentionally *not* in the IR

- **Tempo / time signature / lyrics.** Those are properties of the MIDI file, not the sound bank.
- **Master volume / pan / tune.** Those are synth-level state set by SysEx, not bank-level configuration.
- **Reverb / chorus algorithm parameters.** Those belong on the synth (callers configure `Synthesizer.Reverb.RoomSize` etc.), not in each zone.
- **Per-format raw data.** No "original SF2 generators" backing field, no "original SFZ opcode dictionary." If a feature can't be expressed in the IR, the loader either translates it or drops it with a load-time warning — but the IR doesn't carry format-specific escape hatches.

## Open questions for the loader implementations

These don't block IR design, but the per-format translators in `Loader/Loader/` will need answers:

1. **`SfzBankLoader` — `LFOSettings.PitchDepthCents` vs `ModulationLfoPitchDepthCents` via routes.** SFZ has `lfoN_pitch=` (direct depth) and `lfoN_pitch_oncc<n>=` (CC-modulated depth). The direct depth goes on `LFOSettings`; the CC-modulated depth becomes a route. Worth being explicit when writing `SfzOpcodeTranslator`.
2. **`DlsBankLoader` — connection blocks → routes mapping.** DLS uses a (source, control, destination, transform) tuple format that's very close to our `ModulationRoute`. The conversion should be near-1:1; nail down the source/destination enum mapping when `DlsArticulationTranslator` is written.
3. **`Sf3BankLoader` — is the IR identical between SF2 and SF3?** Yes — only `ISampleSource` differs (Vorbis-decoding sample source vs raw-int16 sample source). The IR doesn't need an SF3-specific field, and `Sf3BankLoader` shares its zone-translation code with `Sf2BankLoader`; only the sample-source factory differs.
4. **Empty patches:** if a bank declares `(Bank=5, Program=12)` but its zones list is empty, should `FindPatch` return the empty patch or null? Probably the empty patch — distinguishes "explicitly defined but silent" from "undefined."
