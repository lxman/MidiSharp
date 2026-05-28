# Synth Genericization — Step-by-Step Refactor

How to take the current SF2-shaped `Voice` and `Synthesizer` and turn them into a format-agnostic engine that consumes the IR from [`sound-bank-ir.md`](sound-bank-ir.md) via the loader from [`sound-bank-loader.md`](sound-bank-loader.md), without ever breaking playback. Each step ships independently. Tests stay green at every commit.

## Guiding constraints

1. **No big-bang.** Every step leaves a working synth that still renders Jump! within tolerance of the previous build.
2. **Old and new paths coexist** during the transition. `LoadSoundFont(SoundFont)` keeps working; `LoadSoundFont(SoundBank)` is added alongside.
3. **Audio output is bit-identical (modulo float rounding) at every step.** A regression test renders Jump! to WAV at the start of each step and diffs against a snapshot from the previous step. If the FFT bands don't match within 0.1 dB, something moved that shouldn't have.
4. **Step 7 (final cleanup) is the only step that deletes code.** All earlier steps add code without removing anything.

## Step 1 — Define IR types

Already specified in `sound-bank-ir.md`. Concretely this step writes:

```
src/MidiSharp.Core/SoundBank/
  SoundBank.cs              Top-level container + IDisposable plumbing
  SoundBankLoader.cs        Static dispatch entry point + registration API
  Patch.cs                  Patch class with FindPatch
  PatchZone.cs              The big record
  Activation.cs             KeyRange, VelocityRange, CCGate, KeySwitch, RoundRobin
  Pitch.cs                  PitchSettings, LevelSettings
  Envelope.cs               EnvelopeSettings
  Lfo.cs                    LFOSettings
  Filter.cs                 FilterSettings, FilterType
  Routes.cs                 ModulationRoute, ModSource (records), ModDestination, ModTransform
  SampleRef.cs              SampleRef, LoopMode
  SampleSource.cs           ISampleSource, SampleMetadata, PreDecodedFloatSampleSource
  Format.cs                 SoundBankFormat enum, SoundBankLoadOptions, exceptions
```

No code changes outside this new directory. `MidiSharp.Core` doesn't reference any `<format>.Net` reader nor the `Loader` project — dispatch and translators all live in `Loader/Loader/`, which depends on `MidiSharp.Core` rather than the reverse. The synth doesn't reference any of this yet. Tests: just compile. CI green.

**Done when:** `dotnet build` produces `MidiSharp.Core.dll` with the new types and zero changes anywhere else.

## Step 2 — Add the SF2 translator and dispatch entry point inside `Loader/Loader/`

Translate the existing `SF2.Net.SoundFont` into a `SoundBank`. This step uses the already-existing `Loader/Loader/Loader.csproj` (which currently just `ProjectReference`s the four `<format>.Net` readers). Add a `MidiSharp.Core` reference so the IR types are visible, then populate:

```
Loader/Loader/
  Loader.csproj                    Add ProjectReference to MidiSharp.Core
  SoundBankLoader.cs               Public entry point: Load(path) / Load(stream, format)
  Format/
    FormatDetector.cs              Magic-byte sniffing + extension fallback
  Sf2/
    Sf2BankLoader.cs               Top-level: walks SF2 hierarchy, produces SoundBank
    Sf2ZoneTranslator.cs           SF2 zone → PatchZone, emits default modulator routes
    Sf2UnitConversions.cs          timecents/cents/cB conversion helpers
    MemoryMappedSf2SampleSource.cs Placeholder; populated in Step 3
```

The Sf2 subfolder is the one place SF2's signed-short encoding meets the domain-typed IR. All the conversion formulas live here:

```csharp
// Timecents → seconds (SF2 envelope times, LFO delays)
private static double TimecentsToSeconds(short tc)
    => tc <= -12000 ? 0.0 : Math.Pow(2.0, tc / 1200.0);

// Absolute cents → Hz (filter cutoff, LFO frequency)
private static double AbsoluteCentsToHz(short cents)
    => 8.176 * Math.Pow(2.0, cents / 1200.0);

// Centibels → dB (attenuation, sustain levels)
private static double CentibelsToDb(short cb) => cb / 10.0;

// SF2 0.1% units → 0..1 fraction (effect sends)
private static double TenthOfPercentToFraction(short v) => v / 1000.0;

// Centibels of attenuation → linear sustain level (volume envelope)
// SF2 SustainVolEnv is "centibels below peak"; convert to a 0..1 multiplier
private static double SustainCbToLinear(short cb)
    => cb <= 0 ? 1.0 : Math.Pow(10.0, -cb / 200.0);
```

The loader walks the SF2 preset/instrument/zone hierarchy once, applies the SF2 "instrument-set, preset-add" semantics, and emits flat `PatchZone` records. The EMU 0.4 factor lives here too:

```csharp
private const double EmuAttenuationFactor = 0.4;
// Apply during InitialAttenuation accumulation:
attenuationCb += gen.Amount.Signed * EmuAttenuationFactor;
```

The SF2 default modulators become routes `Sf2ZoneTranslator` emits with every zone:

```csharp
private static readonly ModulationRoute[] DefaultModulators = new[]
{
    // #1: Velocity → InitialAttenuation, amount 960 cB, concave-unipolar-negative
    new ModulationRoute {
        Source = new ModSource.Velocity(),
        Dest = ModDestination.AttenuationDb,
        Amount = 96.0,
        Transform = ModTransform.ConcaveUnipolarNegative,
    },
    // #2: Velocity → InitialFilterFc, amount -2400 cents, concave-unipolar-negative
    new ModulationRoute {
        Source = new ModSource.Velocity(),
        Dest = ModDestination.FilterCutoffCents,
        Amount = -2400.0,
        Transform = ModTransform.ConcaveUnipolarNegative,
    },
    // #3-#5: pressure, mod wheel, poly pressure → VibratoLfoPitchDepthCents
    new ModulationRoute {
        Source = new ModSource.ChannelPressure(),
        Dest = ModDestination.VibratoLfoPitchDepthCents,
        Amount = 50.0,
        Transform = ModTransform.Linear,
    },
    new ModulationRoute {
        Source = new ModSource.ChannelController(1),
        Dest = ModDestination.VibratoLfoPitchDepthCents,
        Amount = 50.0,
        Transform = ModTransform.Linear,
    },
    new ModulationRoute {
        Source = new ModSource.PolyPressure(),
        Dest = ModDestination.VibratoLfoPitchDepthCents,
        Amount = 50.0,
        Transform = ModTransform.Linear,
    },
    // #6-#10: volume, pan, expression, CC91, CC93 → ... (concrete routes)
};
```

The synth doesn't consume `SoundBank` yet. This step makes `SoundBankLoader.Load("foo.sf2")` (in the `Loader` project) return a valid `SoundBank`, but `Synthesizer` ignores that path and keeps using `SF2.Net` directly via its existing API.

**Done when:** `var bank = SoundBankLoader.Load("foo.sf2")` produces a `SoundBank` whose `Patches`, `PatchZone`s, and `ISampleSource` look right under a debugger. No synth behavior changes. The existing 36-test suite still passes.

## Step 3 — Domain-typed setters on `Envelope`, `LFO`, `LowPassFilter`

Add overloads alongside the existing SF2-unit methods. Old methods unchanged; new methods are what the eventual rewritten Voice will call.

### `Envelope`

```csharp
public sealed class Envelope
{
    // EXISTING (kept):
    public void SetParameters(short delay, short attack, short hold, short decay,
                              short sustain, short release, ...)
    {
        SetParameters(
            delaySeconds:   TimecentsToSeconds(delay),
            attackSeconds:  TimecentsToSeconds(attack),
            holdSeconds:    TimecentsToSeconds(hold),
            decaySeconds:   TimecentsToSeconds(decay),
            sustainLevel:   SustainCbToLinear(sustain),
            releaseSeconds: TimecentsToSeconds(release),
            ...);
    }

    // NEW (preferred going forward):
    public void SetParameters(
        double delaySeconds, double attackSeconds, double holdSeconds,
        double decaySeconds, double sustainLevel, double releaseSeconds,
        double keynumToHoldCentsPerKey = 0, double keynumToDecayCentsPerKey = 0)
    {
        _delaySamples = (int)(delaySeconds * _sampleRate);
        _attackRate   = attackSeconds > 0 ? 1.0 / (attackSeconds * _sampleRate) : double.PositiveInfinity;
        _holdSamples  = (int)(holdSeconds * _sampleRate);
        _decayRate    = decaySeconds > 0 ? -CentibelsPerSecondForFullDecay / (decaySeconds * _sampleRate) : double.NegativeInfinity;
        _sustainLevel = sustainLevel;
        _releaseRate  = releaseSeconds > 0 ? -CentibelsPerSecondForFullDecay / (releaseSeconds * _sampleRate) : double.NegativeInfinity;
        ...
    }

    private static double TimecentsToSeconds(short tc) => tc <= -12000 ? 0.0 : Math.Pow(2.0, tc / 1200.0);
    private static double SustainCbToLinear(short cb) => cb <= 0 ? 1.0 : Math.Pow(10.0, -cb / 200.0);
}
```

The old method now delegates to the new. SF2's "set sustain in cB below peak" gets converted to "linear amplitude" at the boundary; the envelope state machine internally already runs in linear amplitude space.

### `LowFrequencyOscillator`

```csharp
// EXISTING (kept):
public void SetParameters(short delayTimecents, short freqCents)
{
    SetParameters(
        delaySeconds: TimecentsToSeconds(delayTimecents),
        frequencyHz:  AbsoluteCentsToHz(freqCents));
}

// NEW:
public void SetParameters(double delaySeconds, double frequencyHz)
{
    _delaySamples = (int)(delaySeconds * _sampleRate);
    _frequency = frequencyHz;
    _phaseIncrement = 2.0 * Math.PI * _frequency / _sampleRate;
    _delayCounter = _delaySamples;
}
```

### `LowPassFilter`

```csharp
// EXISTING (kept):
public void SetParameters(short cutoffCents, short resonanceCentibels)
{
    SetParameters(
        cutoffHz: AbsoluteCentsToHz(cutoffCents),
        resonanceDb: resonanceCentibels / 10.0);
}

// NEW:
public void SetParameters(double cutoffHz, double resonanceDb)
{
    _cutoffFrequency = Math.Clamp(cutoffHz, 20, _sampleRate * 0.45);
    _resonance = Math.Pow(10.0, resonanceDb / 40.0);  // dB → Q factor
    _resonance = Math.Clamp(_resonance, 0.5, 40.0);
    _enabled = _cutoffFrequency < _sampleRate * 0.45;
    if (_enabled) CalculateCoefficients();
}
```

### Test for this step

Existing 36 tests still pass (they use the old SF2-unit overloads). Add ~6 new unit tests confirming the domain-typed overloads produce identical state to the SF2-unit calls when fed equivalent values:

```csharp
[Fact]
public void Envelope_DomainTypedMatchesSf2Units()
{
    var a = new Envelope(44100);
    a.SetParameters(delay: -12000, attack: -7973, hold: -12000, decay: -1000,
                    sustain: 600, release: -2000);

    var b = new Envelope(44100);
    b.SetParameters(delaySeconds: 0.0, attackSeconds: 0.01, holdSeconds: 0.0,
                    decaySeconds: 0.5, sustainLevel: 0.5, releaseSeconds: 0.25);

    // Internal state should match within float epsilon
    Assert.Equal(a.DelaySamples, b.DelaySamples);
    Assert.Equal(a.AttackRate, b.AttackRate, precision: 6);
    // ...
}
```

**Done when:** new overloads exist, tests pass, no audio behavior change.

## Step 4 — Add `Voice.Configure(PatchZone, ISampleSource, ...)` overload

The new path: Voice can be configured from an IR zone. The old path: still works.

```csharp
public sealed class Voice
{
    // EXISTING (unchanged):
    public void Configure(float[] sampleData, SampleHeader header, Zone instrumentZone, ...) { ... }

    // NEW:
    public void Configure(
        PatchZone zone,
        ISampleSource sampleSource,
        int keyNumber, int velocity, int channel, int generationId)
    {
        // Reset state (same as old Configure)
        _volumeEnvelope.Reset();
        _modulationEnvelope.Reset();
        _modulationLfo.Reset();
        _vibratoLfo.Reset();
        _filter.Reset();
        _position = 0;

        _keyNumber = keyNumber;
        _velocity = velocity;
        _channel = channel;
        _generationId = generationId;
        _state = VoiceState.Playing;

        // Sample reference
        var meta = sampleSource.Metadata(zone.Sample.SampleId);
        _sampleId = zone.Sample.SampleId;
        _sampleSource = sampleSource;
        _sampleStart = zone.Sample.StartOffset ?? 0;
        _sampleEnd = zone.Sample.EndOffset ?? meta.LengthFrames;
        _loopStart = zone.Sample.LoopStartOffset ?? meta.LoopStartFrames;
        _loopEnd = zone.Sample.LoopEndOffset ?? meta.LoopEndFrames;
        _baseSampleRate = meta.SampleRate;
        _rootKey = zone.Sample.OverridingRootKey ?? meta.RootKey;
        _pitchCorrectionCents = meta.PitchCorrectionCents;
        _loopMode = zone.Sample.LoopMode;

        // Pitch / level
        _coarseTuneSemitones = zone.Pitch.CoarseTuneSemitones + zone.Sample.CoarseTuneSemitones;
        _fineTuneCents = zone.Pitch.FineTuneCents + zone.Sample.FineTuneCents;
        _scaleTuningCentsPerKey = zone.Sample.ScaleTuningCentsPerKey;
        _attenuationDb = zone.Level.AttenuationDb;
        _panNormalized = zone.Level.Pan;

        // Envelopes — domain-typed call (Step 3)
        _volumeEnvelope.SetParameters(
            zone.VolumeEnvelope.DelaySeconds, zone.VolumeEnvelope.AttackSeconds,
            zone.VolumeEnvelope.HoldSeconds, zone.VolumeEnvelope.DecaySeconds,
            zone.VolumeEnvelope.SustainLevel, zone.VolumeEnvelope.ReleaseSeconds,
            zone.VolumeEnvelope.KeynumToHoldCentsPerKey,
            zone.VolumeEnvelope.KeynumToDecayCentsPerKey);
        _volumeEnvelope.Trigger(keyNumber);

        if (zone.ModulationEnvelope is { } me)
        {
            _modulationEnvelope.SetParameters(me.DelaySeconds, me.AttackSeconds,
                me.HoldSeconds, me.DecaySeconds, me.SustainLevel, me.ReleaseSeconds);
            _modulationEnvelope.Trigger(keyNumber);
            _hasModEnvelope = true;
        }
        else
        {
            _hasModEnvelope = false;
        }

        // LFOs
        if (zone.VibratoLFO is { } vlfo)
        {
            _vibratoLfo.SetParameters(vlfo.DelaySeconds, vlfo.FrequencyHz);
            _vibratoLfo.Trigger();
            _vibLfoToPitch = vlfo.PitchDepthCents;
            _hasVibLfo = true;
        }
        else { _hasVibLfo = false; }

        if (zone.ModulationLFO is { } mlfo) { /* similar */ }

        // Filter
        if (zone.Filter is { } f)
        {
            _filter.SetParameters(f.CutoffHz, f.ResonanceDb);
            _modEnvToFilterFc = f.EnvelopeDepthCents;
            _modLfoToFilterFc = f.LfoDepthCents;
            _hasFilter = true;
        }
        else { _hasFilter = false; }

        // Sends
        _reverbSend = zone.ReverbSend;
        _chorusSend = zone.ChorusSend;

        // Routes — stash for Process() to consume per-block (see Step 5)
        _routes = zone.Routes;

        _exclusiveGroup = zone.ExclusiveGroup ?? 0;
    }
}
```

A few things to notice:

- `_sampleData` (the shared float array) is replaced by `_sampleSource` + `_sampleId`. The interpolator will go through `_sampleSource.ReadFrames(...)` instead of indexing the array. That migration happens in Step 6; for now the new `Configure` stores the source but Voice.Process still expects `_sampleData`. So this step also adds a `PreDecodedFloatSampleSource` shim that wraps a `float[]` and the new Configure uses it transparently.
- Voice grows new domain-typed fields (`_attenuationDb`, `_panNormalized`, etc.) alongside the old SF2-unit ones (`_attenuation`, `_pan`). The new Configure populates the new fields; the old Configure populates the old fields. Process() temporarily reads from whichever set is populated, gated by a flag. Ugly but transient.

The Synthesizer.NoteOn path now branches based on whether a SoundBank was loaded:

```csharp
public void NoteOn(int channel, int key, int velocity)
{
    // ... validation ...
    if (_soundBank != null)
    {
        NoteOnIR(channel, key, velocity);
    }
    else
    {
        NoteOnLegacy(channel, key, velocity);  // existing code
    }
}

private void NoteOnIR(int channel, int key, int velocity)
{
    var patch = _soundBank.FindPatch(channelState.Bank, channelState.Program)
             ?? _soundBank.FindPatch(0, channelState.Program);
    if (patch == null) return;

    foreach (var zone in patch.Zones)
    {
        if (!zone.Keys.Contains(key)) continue;
        if (!zone.Velocities.Contains(velocity)) continue;
        // (CC gates, keyswitch, round-robin checks — Step 5)
        var voice = AllocateVoice(channel, key);
        if (voice == null) continue;
        if (zone.ExclusiveGroup is int eg && eg > 0)
            KillVoicesByExclusiveClass(channel, eg);
        voice.Configure(zone, _soundBank.Samples, key, velocity, channel, ++_generationCounter);
    }
}
```

### Test for this step

Render Jump! through both paths (old via `LoadSoundFont(SoundFont)`, new via `LoadSoundFont(Sf2Loader.Load(soundFont))`). FFT-compare the two outputs. Tolerance: ±0.5 dB per band. If anything's worse, the SF2 loader or the new Configure is wrong.

**Done when:** rendering the same MIDI + SF2 via either path produces audio that FFT-compares within tolerance.

## Step 5 — Route evaluator

This is where the synth stops hardcoding SF2 default modulators and starts driving everything from `zone.Routes`.

### The data flow

For each playing voice, every audio block (~256 samples):

1. Compute the *current value* of each `ModSource` (velocity is constant; channel pressure / CCs come from `ChannelState`).
2. Apply each `Transform` to its source.
3. Multiply by `Amount`.
4. Add to the running total for each `ModDestination`.
5. Apply destination totals as offsets to the voice's static parameters.

### Source evaluation

```csharp
private double EvaluateSource(ModSource source, ChannelState channelState, Voice voice)
{
    return source switch
    {
        ModSource.Velocity =>           voice.Velocity / 127.0,
        ModSource.KeyNumber =>          voice.KeyNumber / 127.0,
        ModSource.ChannelPressure =>    channelState.ChannelPressure / 127.0,
        ModSource.PolyPressure =>       voice.PolyPressureValue / 127.0,
        ModSource.PitchBend =>          (channelState.PitchBend - 8192) / 8192.0,
        ModSource.ChannelController cc => channelState.GetCC(cc.Number) / 127.0,
        ModSource.NoConnection =>       0.0,
        _ => 0.0,
    };
}
```

All sources are normalized to `[0, 1]` (unipolar) or `[-1, +1]` (pitch bend); the transform decides bipolarity.

### Transform application

```csharp
private double ApplyTransform(double x, ModTransform transform) => transform switch
{
    ModTransform.Linear =>                    x,
    ModTransform.LinearBipolar =>             2 * x - 1,
    ModTransform.ConcaveUnipolar =>           ConcaveCurve(x),
    ModTransform.ConvexUnipolar =>            1 - ConcaveCurve(1 - x),
    ModTransform.Switch =>                    x >= 0.5 ? 1 : 0,
    ModTransform.ConcaveUnipolarNegative =>   -ConcaveCurve(1 - x),  // Used for velocity→atten
    _ => x,
};

private static double ConcaveCurve(double x)
{
    // SF2 §8.1.4 figure E: log-shaped, asymptotic to 1
    if (x <= 0) return 0;
    if (x >= 1) return 1;
    return -40.0 / 96.0 * Math.Log10(x);  // Maps 1→0, 0.5→0.125-ish, 0→1
}
```

The concave curve is the one SF2 detail that bleeds into the synth, because it's how velocity→attenuation produces the dynamic range piano needs. Worth a comment explaining why this specific shape.

### Per-block route summation

Once per audio block, accumulate route contributions into destination buckets:

```csharp
private void EvaluateRoutes(ChannelState ch, Voice voice, out RouteContributions out_)
{
    out_ = default;
    foreach (var route in voice.Routes)
    {
        var srcValue = EvaluateSource(route.Source, ch, voice);
        var transformed = ApplyTransform(srcValue, route.Transform);
        var contribution = transformed * route.Amount;

        switch (route.Dest)
        {
            case ModDestination.PitchCents:                       out_.PitchCents += contribution; break;
            case ModDestination.FilterCutoffCents:                out_.FilterCutoffCents += contribution; break;
            case ModDestination.AttenuationDb:                    out_.AttenuationDb += contribution; break;
            case ModDestination.VibratoLfoPitchDepthCents:        out_.VibLfoPitchDepth += contribution; break;
            // ... etc
        }
    }
}

private struct RouteContributions
{
    public double PitchCents;
    public double FilterCutoffCents;
    public double FilterResonanceDb;
    public double AttenuationDb;
    public double PanNormalized;
    public double VibLfoPitchDepth;
    public double ModLfoPitchDepth;
    public double ModLfoVolumeDb;
    public double ModLfoFilterDepth;
    public double ReverbSendAmount;
    public double ChorusSendAmount;
}
```

`RouteContributions` is a value type — zero allocation per block. Voice.Process consumes it directly:

```csharp
EvaluateRoutes(channelState, voice, out var contrib);

// In the per-sample loop:
double pitchCents = basePitchCents + pitchBendCents + contrib.PitchCents + ...;
double filterModCents = ... + contrib.FilterCutoffCents;
double attenuationDb = _attenuationDb + contrib.AttenuationDb;
double effectiveVibDepth = _vibLfoToPitch + contrib.VibLfoPitchDepth;
// etc.
```

### What this replaces

- `_velocityAttenuation = 400.0 * Math.Log10(127.0 / vel)` — no longer hardcoded; the SF2 loader's default-modulator route for velocity→AttenuationDb produces the same value via the concave transform.
- The hardcoded `(channelState.Modulation/127 + channelState.ChannelPressure/127) * 50` in `Synthesizer.Generate` — replaced by routes (CC1 → VibratoLfoPitchDepthCents, ChannelPressure → VibratoLfoPitchDepthCents, both Linear transform amount 50).
- `Voice.SetReverbSend(float)` / `_reverbSend` as a free-floating value — augmented by route contributions per block.

### Sanity-check arithmetic

The SF2 default modulator for velocity → attenuation: amount 960 cB, concave-unipolar-negative transform. In our IR units: amount **96 dB**, ConcaveUnipolarNegative transform.

At velocity=127: source=1.0, ConcaveCurve(0)=0, ConcaveUnipolarNegative= -0 = 0. Contribution = 0 * 96 = **0 dB**. ✓
At velocity=64:  source≈0.504, ConcaveCurve(0.496)≈0.124, ConcaveUnipolarNegative= -0.124. Contribution = -0.124 * 96 ≈ **-11.9 dB**. Existing formula: `400 * log10(127/64) ≈ 11.9 cB`... wait, that's centibels, so 1.19 dB. Off by 10×.

That kind of unit-conversion bug is exactly what step 4's FFT regression test catches. **Worth running the regression test after every route-related change**, not just at the end of the step. The arithmetic between SF2's encoding and our domain units is the single highest-risk part of the whole refactor.

### Test for this step

The same FFT regression from Step 4, but with the route evaluator active. Plus:

- Unit test: send a fixed-velocity NoteOn and verify the output amplitude matches the expected attenuation curve.
- Unit test: ramp CC1 from 0 to 127 over a held note; verify the vibrato depth scales linearly.
- Unit test: a zone with no routes produces output identical to a zone with all-zero routes (perf assumption).

**Done when:** Jump! still renders within ±0.5 dB of the original, and the hardcoded SF2-default-modulator calls in `Voice.Configure` and `Synthesizer.Generate` can be safely deleted (do it in Step 7).

## Step 6 — `ISampleSource` migration

Replace `Voice._sampleData[index]` with `_sampleSource.ReadFrames(_sampleId, offset, scratchBuffer)`. The naive approach (one virtual call per sample) is catastrophically slow. The fix: a scratch buffer.

### Scratch buffer design

```csharp
private struct SampleCache
{
    public float[] Buffer;              // size = ScratchSize
    public long BaseFrame;              // frame index of Buffer[0] in the source
    public int FramesAvailable;         // <= ScratchSize; less near end of sample
    public bool LoopWrapped;            // true if Buffer contains loop-wrap data
}

private const int ScratchSize = 256;   // tunable; must be >= 2 × interpolation half-width
private SampleCache _cache;
```

The interpolator (`GetInterpolatedSample`) needs `_position - 3` through `_position + 3` (7-tap sinc, half-width 3). The scratch holds 256 frames; while `_position` stays inside `[BaseFrame + 3, BaseFrame + FramesAvailable - 3]` we have a cache hit and read directly from `_cache.Buffer`. When `_position` approaches the edge, refill.

```csharp
private double GetInterpolatedSample()
{
    int index = (int)_position;
    double frac = _position - index;

    // Ensure the interpolation window fits in the scratch buffer
    int windowStart = index - 3;
    int windowEnd = index + 3;
    if (windowStart < _cache.BaseFrame ||
        windowEnd >= _cache.BaseFrame + _cache.FramesAvailable)
    {
        RefillScratchAround(index);
    }

    int local = index - (int)_cache.BaseFrame;
    var buf = _cache.Buffer;
    return coeffs[baseOff + 0] * buf[local - 3]
         + coeffs[baseOff + 1] * buf[local - 2]
         + ...
         + coeffs[baseOff + 6] * buf[local + 3];
}

private void RefillScratchAround(int centerFrame)
{
    // Choose a window: try to put the access point near the middle for amortization.
    long fillStart = Math.Max(0, centerFrame - ScratchSize / 4);
    _cache.BaseFrame = fillStart;
    _cache.FramesAvailable = _sampleSource.ReadFrames(
        _sampleId, fillStart, _cache.Buffer.AsSpan());
}
```

Cost analysis: scratch refills happen at most once per 250-ish samples (the playback advances at a rate ≤ 1 sample per output sample, often slower due to pitch shifting downward). So one virtual call to `ReadFrames` per ~250 samples instead of per sample. Negligible overhead.

### Looping with the scratch buffer

The trickier case: when playback crosses `_loopEnd`, the interpolator needs frames from both before and after the loop point. Two options:

1. **Refill the scratch with loop-aware data:** when `centerFrame` is near `_loopEnd`, read `[centerFrame - 4, _loopEnd]` plus `[_loopStart, _loopStart + (ScratchSize - frames_read)]` into the buffer contiguously.
2. **Branch in the interpolator:** detect the loop boundary; fetch the wrap-around samples one by one.

Option 1 is faster but messier (the `ISampleSource.ReadFrames` interface doesn't know about loops; the Voice has to compose two reads). Option 2 is cleaner but adds a per-sample branch. Probably go with option 1 — looping is the common case and we want the inner loop tight.

### Mono vs. stereo

For native stereo samples (SFZ), `Metadata.Channels == 2`. The scratch buffer holds `2 × ScratchSize` floats interleaved. The interpolator runs twice (once per channel) using the same fractional position. Output is true stereo before pan, not a mono signal panned.

For SF2 `SampleLink` paired mono, the loader emits two `PatchZone`s — one per channel — with linked sample IDs and matched envelope state. The synth allocates two voices that share an envelope state object. (This is the trickiest case; defer until SF2 paired-stereo content actually comes up.)

### Test for this step

Jump! regression FFT. Plus:

- Render a synthesizer test card (single sustained note) through both old (direct `_sampleData[index]`) and new (scratch buffer) paths. Bit-compare the output.
- Stress test: 64-voice polyphony with rapid pitch sweeps. Measure CPU. Should be within ±10% of the old direct-array path.

**Done when:** the scratch buffer code is the only sample-access path Voice uses, the existing tests pass, and CPU hasn't regressed.

## Step 7 — Cleanup

Now (and only now) the deletes happen.

- Remove `Voice._sampleData` (the float[] field) and the old `Configure(float[], SampleHeader, Zone, ...)` overload.
- Remove `Voice.ApplyGenerator` entirely.
- Remove the old SF2-unit overloads on `Envelope`, `LFO`, `LowPassFilter` (the new domain-typed ones cover all callers).
- Remove the `NoteOnLegacy` branch in `Synthesizer.NoteOn`; rename `NoteOnIR` to just `NoteOn` private impl.
- Remove `_velocityAttenuation` and the velocity→filter calc from `Voice.Configure` — routes handle them.
- Remove the EMU_ATTENUATION_FACTOR constant and its application — it lives in `Sf2Loader` now.
- Remove the hardcoded channel default modulator computations in `Synthesizer.Generate`.
- Remove `Voice._attenuation` (centibels field) since `_attenuationDb` (dB field) is the only one used.
- Update `Synthesizer.LoadSoundFont(SoundFont)` to internally route through the SF2 translator inside the `Loader` project and call `LoadSoundFont(SoundBank)`. The public API still accepts `SF2.Net.SoundFont` for backwards compatibility; internally it's all IR. MidiSharp.Synth's `ProjectReference` on `SF2.Net` can now be replaced with one on `Loader` (or dropped entirely if the demo project takes on that reference instead — the synth itself no longer needs to know SF2 exists).

After this step, the synth has *one* code path for everything. `Voice.cs` is probably 200-300 lines shorter. Every unit is documented at its declaration site. New format support requires zero synth changes.

### Test for this step

Full test suite, FFT regression on all four A/B reference MIDIs (Jump!, Breakout, J-cycle, Tyroland), polyphony stress test, plus a manual listening pass.

**Done when:** all tests pass, A/B FFTs match the pre-refactor versions within ±0.3 dB across the board, and you can explain every public field of `Voice` without using the words "timecents" or "centibels."

## Cross-cutting test strategy

Throughout all steps:

1. **Jump! FFT snapshot per step.** Commit one WAV per step into a `regression/` folder. Each PR diffs against the previous step's snapshot. Tolerance: ±0.3 dB per band, ±0.5 dB total RMS.
2. **The 36 existing unit tests stay green.** Add new ones; don't delete existing.
3. **Polyphony stress** at the end of each step: render a heavy MIDI and confirm CPU hasn't regressed. Spot-check `dotnet trace` for unexpected allocations.
4. **Listening sanity check** at the end of each step. Play Jump! and one solo piano piece. If something *sounds* different even if FFT agrees, the test catches what numbers miss (transient phase, etc.).

## Estimated effort

| Step | Project(s) touched | Effort | Risk |
|---|---|---|---|
| 1: IR types | `MidiSharp.Core` (new `SoundBank/` dir) | 2-3 days | Low — pure type definitions |
| 2: SF2 translator + dispatch | `Loader/Loader/` (new `Sf2/` subfolder + `SoundBankLoader.cs`) | 4-6 days | Medium — unit conversions, default modulator translation |
| 3: Domain-typed setters | `MidiSharp.Synth` (Envelope/LFO/Filter) | 2-3 days | Low — delegating overloads |
| 4: Voice.Configure(PatchZone) overload | `MidiSharp.Synth` | 3-5 days | Medium — dual-path Voice state is messy |
| 5: Route evaluator | `MidiSharp.Synth` | 3-5 days | **High** — arithmetic bugs are easy and audible |
| 6: ISampleSource migration | `MidiSharp.Synth` + `Loader/Loader/Sf2/` | 4-6 days | Medium-high — scratch buffer correctness matters; loop edge cases |
| 7: Cleanup + delete | `MidiSharp.Synth` + csproj reference cleanup | 1-2 days | Low — but lots of greens have to stay green |

Total: 3-4 weeks of focused work. Each step is a separate PR. Each PR ships green and rendering correctly.

After step 7, adding SF3 / SFZ / DLS support means filling in the corresponding subfolders inside `Loader/Loader/` (`Sf3/`, `Sfz/`, `Dls/`) — each one is mechanical translation work, and no synth changes are needed because the IR is already format-neutral.

## What this refactor does *not* do

- Doesn't change MIDI surface (RealtimePlayer, dispatch logic).
- Doesn't change reverb, chorus, master volume, master pan handling.
- Doesn't change the public API of `Synthesizer` (existing `LoadSoundFont(string)` still works; just internally translates through `Sf2Loader`).
- Doesn't add SFZ or DLS support — that's the *next* set of work, which becomes trivial once the synth is format-agnostic.
- Doesn't switch from synchronous decode to mmap. That's an `ISampleSource` implementation change, orthogonal to this refactor.

## When to start

Probably not until at least one of these motivators shows up:
- You want SF3 (Vorbis SF2) playback, and want to avoid SF2-specific hacks for it.
- You want to start SFZ implementation and the IR makes that work tractable.
- A bug surfaces that's genuinely caused by the SF2-shape of the synth (none have so far).
- You want to deploy on mobile and the mmap'd sample-source story matters.

Until then, the existing SF2-shaped synth is fine — it works, it's tested, it sounds right. Refactor on a real motivator, not pre-emptively.
