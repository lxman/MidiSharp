# SoundBank Loader — Design

A unified loader that consumes any of {SF2, SF3, SFZ, DLS} and produces what the synth needs: a structural in-memory IR describing every patch/zone/modulation, plus a sample source that streams float audio frames from (typically mmap'd) backing storage on demand.

## Goals

1. **One synth, four formats.** Voice and Synthesizer code never branches on source format. All format quirks live inside the loader.
2. **Mobile-friendly memory.** Structural data resident in RAM (small); sample data backed by mmap or LRU cache (bounded working set, scales with polyphony not library size).
3. **No information loss for the synth's needs.** Modulation routes, envelope parameters, exclusive groups, keyswitch/round-robin etc. all survive translation.
4. **Format-specific features degrade gracefully.** SFZ features SF2 lacks (round-robin, CC-gated regions, keyswitch) ride along as optional fields. SF2-only features (default modulator transforms) are first-class.
5. **Audio-thread safety.** Sample reads are non-blocking, lock-free, and never trigger a synchronous decode unless explicitly warmed.

## High-level shape

```
                    ┌─────────────────────┐
                    │  SoundBankLoader    │
                    │  .Load(path)        │ (in Loader/Loader/, public API)
                    └──────────┬──────────┘
                               │
                               ▼
            ┌───────────────────────────────────────┐
            │   Loader project (Loader/Loader/)     │
            │   Contains all four format translators│
            │                                       │
            │   Sf2BankLoader, Sf3BankLoader,       │
            │   SfzBankLoader, DlsBankLoader        │
            └──────┬────────┬────────┬────────┬─────┘
                   │uses    │uses    │uses    │uses
                   ▼        ▼        ▼        ▼
              ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐
              │SF2.  │ │SF3.  │ │SFZ.  │ │DLS.  │   (pure format readers;
              │ Net  │ │ Net  │ │ Net  │ │ Net  │    no MidiSharp dependency)
              └──────┘ └──────┘ └──────┘ └──────┘
                               │
                               ▼
                    ┌─────────────────────┐
                    │     SoundBank       │   (IR types defined in MidiSharp.Core)
                    │  ┌────────────────┐ │
                    │  │ structural IR  │ │ (in RAM — patches, zones, routes, metadata)
                    │  ├────────────────┤ │
                    │  │ ISampleSource  │ │ (mmap'd or cached — float frames on demand)
                    │  └────────────────┘ │
                    └──────────┬──────────┘
                               │
                               ▼
                    ┌─────────────────────┐
                    │    Synthesizer      │   (consumes both; format-blind)
                    └─────────────────────┘
```

## Project layout for the IR and the loaders

The architecture has two tiers, separating "what's in the soundfont file" from "how to translate it for the synth":

| Project | Path | References | Purpose |
|---|---|---|---|
| `<format>.Net` | `Loader/<format>/<format>.Net/` | — | Pure format reader. No MidiSharp dep. Publishable as standalone NuGet. |
| `Loader` (umbrella) | `Loader/Loader/` | All four `<format>.Net` + `MidiSharp.Core` | Format-to-IR translators; public dispatch API. |
| IR types | `src/MidiSharp.Core/SoundBank/` | — | The IR itself, consumed by the synth and produced by `Loader`. |

### IR types in `MidiSharp.Core`

```
src/MidiSharp.Core/SoundBank/
    SoundBank.cs              Top-level container
    Patch.cs
    PatchZone.cs
    Activation.cs             KeyRange, VelocityRange, CCGate, KeySwitch, RoundRobin
    Pitch.cs                  PitchSettings, LevelSettings
    Envelope.cs               EnvelopeSettings
    Lfo.cs                    LFOSettings
    Filter.cs                 FilterSettings, FilterType
    Routes.cs                 ModulationRoute + ModSource (records) + ModDestination + ModTransform
    SampleRef.cs              SampleRef, LoopMode
    SampleSource.cs           ISampleSource, SampleMetadata, PreDecodedFloatSampleSource
    Format.cs                 SoundBankFormat enum, SoundBankLoadOptions, exceptions
```

`MidiSharp.Core` defines types but doesn't reference any reader. The synth depends only on `MidiSharp.Core` for its IR consumption; it doesn't know `SF2.Net` or `Loader` exist.

### The unified `Loader` project

```
Loader/Loader/
    Loader.csproj             References DLS.Net + SF2.Net + SF3.Net + SFZ.Net + MidiSharp.Core
    SoundBankLoader.cs        Public entry point: static Load(path) / Load(stream, format)
    Format/
        FormatDetector.cs     Magic-byte sniffing + extension fallback
    Sf2/
        Sf2BankLoader.cs      SF2.Net.SoundFont → SoundBank
        Sf2ZoneTranslator.cs  SF2 zone → PatchZone; emits default modulator routes
        Sf2UnitConversions.cs timecents/abs-cents/cB helpers
        MemoryMappedSf2SampleSource.cs
    Sf3/
        Sf3BankLoader.cs      Reuses Sf2ZoneTranslator with a different sample source
        LazyVorbisSf3SampleSource.cs
    Sfz/
        SfzBankLoader.cs
        SfzOpcodeTranslator.cs
        WavFolderSampleSource.cs
    Dls/
        DlsBankLoader.cs
        DlsArticulationTranslator.cs
        DlsWaveTableSampleSource.cs
```

### Public API in the Loader project

```csharp
namespace MidiSharp.SoundBank;     // shared with the IR types in MidiSharp.Core

public static class SoundBankLoader
{
    /// <summary>Load by file path; detect format from magic bytes (fall back to extension).</summary>
    public static SoundBank Load(string path, SoundBankLoadOptions? options = null);

    /// <summary>Load from a stream; format must be specified. SFZ also requires basePath
    /// for resolving relative sample references.</summary>
    public static SoundBank Load(
        Stream stream, SoundBankFormat format,
        string? basePath = null, SoundBankLoadOptions? options = null);
}
```

No registration / plugin mechanism is needed — the Loader project owns all four translators directly and dispatches internally. This is simpler than per-format Loader projects but costs binary size: apps that want only SF2 still pull SF3.Net, SFZ.Net, and DLS.Net in transitively. The format-specific readers are small (kilobytes each), so the tradeoff is favorable for most callers.

If a binary-size-constrained scenario does appear later, the unified Loader can be factored into per-format Loader assemblies without any IR or synth changes — the IR contract is already format-neutral.

## The Loader API

The public entry point is small and intention-revealing:

```csharp
namespace MidiSharp.SoundBank;

public static class SoundBankLoader
{
    /// <summary>
    /// Load a sound bank from disk. Format is detected from extension and verified
    /// against the file's magic bytes. Throws <see cref="UnsupportedFormatException"/>
    /// for unrecognized formats, <see cref="SoundBankLoadException"/> for malformed
    /// content (missing referenced WAVs, corrupted chunks, etc.).
    /// </summary>
    public static SoundBank Load(string path, SoundBankLoadOptions? options = null);

    /// <summary>
    /// Load from an already-open stream. <paramref name="format"/> must be specified
    /// (no magic-byte sniffing for arbitrary streams). For SFZ, the caller must also
    /// supply <paramref name="basePath"/> for resolving relative sample references.
    /// </summary>
    public static SoundBank Load(
        Stream stream,
        SoundBankFormat format,
        string? basePath = null,
        SoundBankLoadOptions? options = null);
}

public sealed class SoundBankLoadOptions
{
    /// <summary>Whether to memory-map sample data (default true). Disable for streams
    /// or when working from non-file sources.</summary>
    public bool MemoryMapSamples { get; init; } = true;

    /// <summary>Maximum RAM (bytes) for decoded sample cache. Only meaningful for SF3
    /// (Vorbis must be decoded; can't be mmap'd directly). Default 64 MB.</summary>
    public long DecodedSampleCacheBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Touch the first page of every sample at load time so subsequent NoteOns
    /// don't fault during the audio callback. Cheap insurance; default true.</summary>
    public bool WarmSampleFirstPages { get; init; } = true;

    /// <summary>Issue OS prefetch hints for samples that just started playing. Reduces
    /// page-fault audio glitches on flash storage. Default true.</summary>
    public bool PrefetchActiveSamples { get; init; } = true;
}

public enum SoundBankFormat { Sf2, Sf3, Sfz, Dls }
```

The result of any successful load is a `SoundBank`:

```csharp
public sealed class SoundBank : IDisposable
{
    public string Name { get; init; }
    public string? Author { get; init; }
    public SoundBankFormat SourceFormat { get; init; }
    public IReadOnlyList<Patch> Patches { get; init; }
    public ISampleSource Samples { get; init; }   // owned by the bank; disposed with it

    public Patch? FindPatch(int bank, int program);
    public void Dispose();   // releases mmap handles, disk file handles, decode caches
}
```

## The structural IR

The IR is what's resident in RAM after a successful load. Designed to be **format-neutral** (no SF2 timecents/centibels leaking out) and **pre-flattened** (loader has already merged SF2 preset/instrument inheritance or SFZ `<global>/<group>/<region>` cascades).

See [`sound-bank-ir.md`](sound-bank-ir.md) for the field-by-field shape. The relevant types referenced below:

- `Patch` — MIDI-addressable (bank, program).
- `PatchZone` — one playable region; pre-flattened from any source hierarchy.
- `EnvelopeSettings` / `LFOSettings` / `FilterSettings` / `ModulationRoute` — all using domain-natural units (seconds, Hz, dB).
- `SampleRef` — points into the bank's sample source by integer ID.

Estimated structural footprint:

| Library size | Approx zones | Structural RAM |
|---|---|---|
| GeneralUser-GS (30 MB SF2) | ~6,000 | 3-5 MB |
| Tyroland (850 MB SF2) | ~25,000 | 15-20 MB |
| 10K-sample orchestral lib | ~60,000 | 40-60 MB |

Sub-1% of total memory in every realistic case.

## The `ISampleSource` interface

The synth's audio loop talks to this and only this for sample bytes. Its contract is intentionally tight:

```csharp
public interface ISampleSource : IDisposable
{
    /// <summary>Number of distinct samples in this bank.</summary>
    int Count { get; }

    /// <summary>Metadata for one sample (rate, length, loop points, root key, etc.).
    /// Cheap to call; backed by RAM-resident structures.</summary>
    SampleMetadata Metadata(int sampleId);

    /// <summary>
    /// Read frames from sample <paramref name="sampleId"/> starting at
    /// <paramref name="frameOffset"/> into <paramref name="dest"/>. Returns the
    /// number of frames actually written (≤ dest.Length).
    ///
    /// Implementations MUST be:
    /// <list type="bullet">
    /// <item>Thread-safe (audio thread + UI thread may call simultaneously).</item>
    /// <item>Non-blocking under normal conditions. Page faults from cold mmap pages
    /// are acceptable; explicit decode work (SF3 Vorbis) must be prefetched.</item>
    /// <item>Allocation-free in the hot path.</item>
    /// </list>
    /// </summary>
    int ReadFrames(int sampleId, long frameOffset, Span<float> dest);

    /// <summary>
    /// Hint that <paramref name="sampleId"/> is about to be played. Implementations
    /// may decode it into cache (SF3), issue an OS prefetch (mmap'd SF2/SFZ/DLS),
    /// or no-op. Called from the audio thread on NoteOn; must return quickly.
    /// </summary>
    void PrepareSample(int sampleId);
}

public sealed class SampleMetadata
{
    public int SampleRate { get; init; }
    public int Channels { get; init; }            // SF2/DLS = 1; SFZ = 1 or 2
    public long Length { get; init; }             // frames
    public long LoopStart { get; init; }          // frame index, sample-relative
    public long LoopEnd { get; init; }            // frame index, sample-relative
    public int RootKey { get; init; }             // 0-127; 255 = unpitched
    public double PitchCorrectionCents { get; init; }
    public string? Name { get; init; }
}
```

**Critical contract details:**

- `frameOffset` is **sample-relative** (frame 0 is the first sample of *that* sample, not the first byte of an SF2 `smpl` chunk). Loaders compute the necessary remapping.
- `dest.Length` is in **frames**, not bytes. Stereo samples interleave L/R inside one frame.
- Return value < `dest.Length` means end-of-sample reached; the synth wraps to the loop or stops based on `Metadata().LoopMode`.

## Per-format implementations

All four format translators live inside the single `Loader/Loader/` project under their respective subfolders (`Sf2/`, `Sf3/`, `Sfz/`, `Dls/`). Each one owns:
- A `BankLoader` class that translates parsed `<format>.Net` types into the SoundBank IR.
- One or more `ISampleSource` implementations appropriate to the format's sample encoding.

The synth doesn't see any of these directly — it only sees the resulting `SoundBank` and the `ISampleSource` it carries.

### SF2 — `Loader/Loader/Sf2/`

Reads via `SF2.Net`; emits IR using `MidiSharp.Core` types.

**`MemoryMappedSf2SampleSource`:**
- mmap the `.sf2` file at load time.
- Locate the `smpl` chunk; remember its offset and length.
- Per sample, store `(absoluteOffsetInChunk, lengthInFrames, loopStart, loopEnd, …)` as `int32`s in a small array.
- `ReadFrames`: index into the mmap'd region as `ReadOnlySpan<short>`, convert int16 → float on the fly (one multiply per sample).
- `PrepareSample`: optional `madvise(WILLNEED)` for the next N pages.

Working set scales with `polyphony × active-sample-duration × 2 bytes per frame`. For 64 voices × 2-second piano samples: ~11 MB resident on a 1 GB SF2.

### SF3 — `Loader/Loader/Sf3/`

Reads via `SF3.Net` (which itself depends on `SF2.Net` — SF3 is structurally SF2 with Vorbis samples). The bank-loading logic shares code with `Sf2BankLoader` (same generator/modulator/zone shape); only the sample source differs.

**`LazyVorbisSf3SampleSource`:**
- mmap the `.sf3` file the same way as SF2.
- Each sample's payload is a Vorbis blob, not raw int16.
- Maintain an LRU cache of decoded float buffers, bounded by `SoundBankLoadOptions.DecodedSampleCacheBytes`.
- `PrepareSample`: enqueue decode work on a background thread (decode is not audio-thread-safe).
- `ReadFrames`: if cached, copy out floats; if not, attempt a quick-decode (rare, only if the synth fires NoteOn without PrepareSample first — log a warning).

This is the one format where pure mmap doesn't suffice — Vorbis is compressed. The cache is the necessary evil; the budget knob lets callers tune for their target.

### SFZ — `Loader/Loader/Sfz/`

Reads via `SFZ.Net` (text parser + opcode model).

**`WavFolderSampleSource`:**
- One mmap handle per referenced `.wav` file. Build a sample ID → mmap mapping at load time.
- Parse each WAV's `fmt ` chunk once; remember offset to `data` chunk and PCM format.
- `ReadFrames`: read from mmap'd `data` chunk, convert from native PCM format (8/16/24/32 int or float) to float on the fly. The 24-bit case is the only awkward one (3-byte stride).
- `PrepareSample`: `madvise(WILLNEED)` like SF2.

SFZ libraries with thousands of samples need a corresponding number of mmap handles — well within OS limits but worth being aware of.

### DLS — `Loader/Loader/Dls/`

Reads via `DLS.Net` (RIFF/DLS parser).

**`DlsWaveTableSampleSource`:**
- mmap the `.dls` file.
- Each sample is a `WAVE` sub-chunk inside the `wvpl` list chunk; remember each one's offset to its `data` sub-chunk plus its `fmt ` info.
- Same per-format conversion strategy as SFZ (PCM bit-depth → float).
- `PrepareSample`: same as SF2/SFZ.

## Memory + performance characteristics

| Tier | Backing | Hot path | Footprint |
|---|---|---|---|
| Structural IR | RAM | Direct field access | 1-60 MB; <1% of total |
| SF2/SFZ/DLS samples | mmap'd file | Page-cache load, int→float per sample | 5-100 MB working set |
| SF3 samples | mmap'd file + LRU decode cache | Cache hit: copy; miss: decode | Capped by `DecodedSampleCacheBytes` |
| Voice state | RAM | Direct field access | ~100 KB total |

On a phone with a 200 MB SF2 library, you'd expect to see ~50 MB resident under load with this design, vs ~400 MB with the current "decode everything to float[] upfront" approach.

## Lifetime, threading, error handling

### Lifetime

- `SoundBank` owns its `ISampleSource`. Disposing the bank disposes the source, which releases mmap handles and file descriptors.
- The synth holds a reference to `SoundBank.Samples` for the duration of any voices it allocated from that bank. **Do not dispose a bank while voices from it are still sounding** — calls to `AllSoundOff` first, then dispose.
- Switching banks mid-piece: load new, swap references atomically, `AllSoundOff` old, dispose old.

### Threading

| Method | Threads | Locking |
|---|---|---|
| `SoundBankLoader.Load` | Loader thread (any) | Internal; not callable from audio thread |
| `SoundBank.FindPatch` | Any thread, read-only after load | None needed (IR is immutable) |
| `ISampleSource.ReadFrames` | **Audio thread** | Lock-free; mmap reads are inherently thread-safe |
| `ISampleSource.PrepareSample` | Audio thread (called from NoteOn) | Lock-free for mmap variants; queues background work for SF3 |
| `ISampleSource.Metadata` | Any thread | None needed (RAM-resident copy) |
| `SoundBank.Dispose` | Owning thread, after `AllSoundOff` | One-shot; not concurrent with audio thread |

The IR is immutable after load. The sample source is conceptually immutable too — caches and prefetch are internal optimizations the API consumer never observes.

### Error handling

**At load time (fail fast):**
- Corrupted RIFF chunks → `SoundBankLoadException` with chunk offset and expected/actual.
- SFZ references a missing `.wav` → `SoundBankLoadException` with the unresolved path.
- SF3 Vorbis header malformed → same.
- Unrecognized magic bytes → `UnsupportedFormatException`.

**At play time (degrade quietly):**
- `ReadFrames` against a corrupt sample (rare; would mean mmap returned bad bytes) → return 0 frames; voice ends naturally.
- SF3 cache eviction during long-held note → background re-decode; if it misses the deadline, voice silences for that block (~5 ms) rather than glitching.
- mmap'd file removed externally (rare; users shouldn't do this) → SIGBUS on Linux/macOS. Document as "don't do that"; we can't catch SIGBUS portably without significant work.

## Is this the right approach? Open considerations

**Yes, with these notes:**

1. **Loader is composable, not monolithic.** Each format gets its own concrete loader; `SoundBankLoader.Load` is a dispatcher. New formats slot in by adding a loader class and registering its magic bytes / extension.

2. **The IR vs sample-source split is the load-bearing decision.** Once that's right, format support is mechanical. Get it wrong (e.g., put sample data in the IR as `float[]`) and you've committed to "decode everything upfront" for everyone.

3. **mmap is the default but not the only option.** Implementations also need a `PreDecodedFloatSampleSource` for callers loading from a `Stream` or who can't mmap (in-memory tests, network sources). The interface accommodates both.

4. **One thing to decide before writing code:** is `ISampleSource` allowed to allocate during `ReadFrames`? Strict answer: no. Pragmatic answer: only on cache miss for SF3, and only via pre-allocated scratch buffers. Worth nailing down so the audio-thread budget stays predictable.

5. **Format-specific extension fields in the IR** (`KeySwitch?`, `RoundRobin?`, `CCGate[]`) should be on `PatchZone` as nullables/empties — not in a separate "SfzExtras" object. Keeps the synth's zone-selection loop uniform: check a field, skip if absent/empty, otherwise apply.

## Implementation roadmap

| Phase | Deliverable | Project(s) touched | Effort |
|---|---|---|---|
| 1 | IR types + `ISampleSource` interface + `PreDecodedFloatSampleSource` shim | `MidiSharp.Core` (new `SoundBank/` dir) | 1-2 days |
| 2 | `Sf2BankLoader` + dispatch entry point; rework Synth to consume IR | `Loader/Loader/` (new `Sf2/` subfolder + `SoundBankLoader.cs`); `MidiSharp.Synth` | 3-5 days |
| 3 | `MemoryMappedSf2SampleSource`; verify same audio output as current code | `Loader/Loader/Sf2/` | 2-3 days |
| 4 | Flesh out `SF3.Net` parser; add `Sf3BankLoader` + `LazyVorbisSf3SampleSource` | `Loader/SF3/SF3.Net/`, `Loader/Loader/Sf3/` | 2-3 days (depends on Vorbis lib choice) |
| 5 | Flesh out `SFZ.Net` parser; add `SfzBankLoader` (opcodes per per-format mapping doc) + `WavFolderSampleSource` | `Loader/SFZ/SFZ.Net/`, `Loader/Loader/Sfz/` | 1-2 weeks |
| 6 | Flesh out `DLS.Net` parser; add `DlsBankLoader` + `DlsWaveTableSampleSource` | `Loader/DLS/DLS.Net/`, `Loader/Loader/Dls/` | 4-7 days |

Total ~4-6 weeks for full multi-format support with mobile-grade memory characteristics. Synth refactor is sequenced inside phase 2; see [`synth-genericization.md`](synth-genericization.md) for the step-by-step breakdown that keeps audio output regression-free at each commit.

## Open questions

1. **Vorbis library for SF3:** stb_vorbis bindings? NLayer-style pure-managed port? .NET's `System.Speech` doesn't cover this. Worth a separate decision before phase 4.
2. **Streaming vs. blocking SF3 decode warmup:** when `PrepareSample` is called for a not-yet-decoded sample, do we block briefly or accept silence on the first audio buffer? Probably the latter, with a configurable "synchronous decode on first NoteOn" debug mode.
3. **Cache eviction policy for SF3:** strict LRU? Or "score by `last used time × file size`" to keep small samples preferentially? LRU is simpler; ship it and measure.
4. **Memory pressure callback:** should `SoundBank` expose an event when the OS issues a memory warning (iOS) so the SF3 cache can self-trim? Probably yes; mark as v2 feature.
