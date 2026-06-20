# Plugin Hosting — Phased Architecture Plan

**Goal:** let MidiSharp **host external audio plugins** — load and run third-party effects and
instruments (CLAP, VST2, VST3, and later LADSPA/DSSI/LV2/AU) inside the existing mixer/effect-rack, as
first-class inserts and sound sources.

**Status:** planning. Nothing here is built yet. This document is the map; each phase ends with a
measured acceptance gate before the next begins.

---

## 1. Guiding decisions (locked)

- **Cross-platform from the start.** The managed core and the loader (`System.Runtime.InteropServices.NativeLibrary`)
  are portable; only per-format *discovery paths* and *bundle layout* are OS-specific. AU is macOS-only
  (deferred until Mac is a real target); AAX is parked (Avid NDA + PACE signing — effectively infeasible
  for an indie host, and only Pro Tools loads it).
- **CLAP is the architectural anchor.** Pure C ABI, MIT-licensed, cross-platform, covers *both* effects
  and instruments with sample-accurate events. It shapes the format-agnostic abstraction against a
  *complete* format. VST2/VST3 are adapters that reuse the same plumbing.
- **LADSPA is the interop spike, not the foundation.** It's the minimal possible ABI — used in Phase 0
  to de-risk the C#↔native realtime boundary before taking on CLAP's larger surface. It is Linux-only and
  effects-only, so it is a *warm-up* (optionally kept as a cheap Linux adapter), never the anchor.
- **Parameter-only hosting first.** The web UI can't embed a native plugin GUI. Every format exposes
  normalized parameters with name/display/label strings — exactly the generic-knob model the web UI
  already builds strips from. Native GUI windows are a separate, deferred, non-web track (Phase 7).
- **In-process first, designed for out-of-process later.** A segfaulting plugin takes down the whole
  .NET process. We accept that initially but keep the `IPluginFormat`/`IHostedPlugin` boundary narrow
  enough that an out-of-process transport (shared-memory audio ring + RPC) can slot behind it without
  touching the engine (Phase 8).

## 2. Invariants (regression guards — write these as tests)

1. **Dormant when unused.** With no plugin loaded, the engine is **bit-identical** to today. Hosted
   effects only exist inside a `ProcessorChain`, which is already bit-identical when empty.
2. **Zero managed allocation in the audio callback.** A loaded plugin's per-block path must not allocate
   on the managed heap. Verified with `GC.GetAllocatedBytesForCurrentThread()` before/after a block ==
   `0`. All buffers (planar channel scratch, parameter cells, event blocks) are pre-allocated unmanaged
   memory owned by the host adapter and reused.
3. **No locks on the audio thread.** Parameter writes, plugin swaps, and chain reorders use the existing
   lock-free snapshot discipline (`ProcessorChain.SetAll`) or atomic scalar writes — never a lock taken
   in `Process`.
4. **Format isolation.** The format-agnostic core (`MidiSharp.Hosting`) has **no** P/Invoke and **no**
   reference to any format SDK. Every native call lives in a per-format adapter behind `IPluginFormat`.

## 3. Where it plugs into the existing engine

MidiSharp already has the exact seam. The contract everywhere is **interleaved stereo, mutated in
place**:

- `MidiSharp.Dsp.IAudioProcessor.Process(Span<float> interleavedStereo)` + `Reset()` — the effect
  contract.
- `MidiSharp.Dsp.ProcessorChain : IAudioProcessor` — ordered chain, lock-free snapshot, `Bypass`,
  atomic `SetAll` reorder.
- `MidiSharp.Synth.IInstrumentInsert.Process(Span<float> interleavedStereo)` — the synth's per-instrument
  hook (synth has no `MidiSharp.Dsp` dependency).
- `samples/MidiSharp.Server/EffectRack.cs` (`: IInstrumentInsert`) wraps a `ProcessorChain` and serves
  both the master bus and per-instrument inserts; `Configure(EffectDto[], trailingGainDb)` builds it.

**A hosted effect becomes an `IAudioProcessor`** and drops straight into a `ProcessorChain` next to the
built-in EQ/limiter/gain — no engine change. **A hosted instrument** feeds a part/bus as an alternative
sound source to `Synthesizer` (bigger; Phase 4).

### The central impedance mismatch — interleaved vs planar

Every plugin format hands audio as **planar (non-interleaved) `float**`** — one buffer per channel
(LADSPA per-port `connect_port`, VST2 `float** inputs/outputs`, CLAP `clap_audio_buffer.data32`,
VST3 `AudioBusBuffers`). MidiSharp's pipeline is **interleaved stereo**. So every hosted-plugin adapter
runs the same RT-safe shuffle each block, against **pre-allocated unmanaged** channel buffers:

```
deinterleave(interleavedStereo) → planar channel buffers
   → plugin.process(planar in, planar out)
   → interleave(planar out) → interleavedStereo   // in place
```

This deinterleave/process/reinterleave kernel is written **once** in the core (`PlanarBridge`) and reused
by every format adapter. It is the single most important piece of cross-cutting plumbing, which is
exactly why Phase 0 de-risks it against the simplest ABI.

## 4. Project layout

```
src/MidiSharp.Hosting/            # format-agnostic managed core — NO P/Invoke
    IHostedPlugin.cs              # lifecycle + process + params + state + events
    IPluginFormat.cs              # scan/discover + load-by-id
    PluginParameter.cs            # normalized 0..1, name/display/label, range/flags
    PluginDescriptor.cs           # id, name, vendor, format, isInstrument, path
    HostedEffect.cs               # IHostedPlugin -> IAudioProcessor adapter (drops into ProcessorChain)
    HostedInstrument.cs           # IHostedPlugin -> mixer sound source (Phase 4)
    PlanarBridge.cs               # RT-safe interleave<->planar over unmanaged buffers
    PluginRegistry.cs             # discovered plugins across all registered formats
    Native/UnmanagedBuffer.cs     # pinned/unmanaged scratch, no-GC helpers

src/MidiSharp.Hosting.Ladspa/     # Phase 0 spike  (Linux .so)
src/MidiSharp.Hosting.Clap/       # Phase 1-4      (cross-platform .clap)
src/MidiSharp.Hosting.Vst2/       # Phase 5        (.so/.dll/.vst)
src/MidiSharp.Hosting.Vst3/       # Phase 6        (.vst3 — C++ ABI bridge)
```

Each adapter is self-contained, registers an `IPluginFormat` with the `PluginRegistry`, and is bundled
into the `MidiSharp.Player` umbrella package once stable. The web server gains a new `"plugin"`
`EffectDto` type and a generic param UI; the engine projects are untouched.

## 5. Core abstraction sketch

```csharp
// MidiSharp.Hosting — format-agnostic, no P/Invoke.
public interface IPluginFormat {
    string Name { get; }                                   // "CLAP", "VST2", "LADSPA"...
    IEnumerable<PluginDescriptor> Scan(IEnumerable<string> searchPaths);
    IHostedPlugin Load(PluginDescriptor descriptor, AudioConfig config);
}

public interface IHostedPlugin : IDisposable {
    PluginDescriptor Descriptor { get; }
    bool IsInstrument { get; }

    void Activate(int sampleRate, int maxBlockFrames);     // allocate unmanaged buffers here
    void Deactivate();

    // RT hot path — NO managed allocation, NO locks. Planar in/out + sample-accurate events.
    void ProcessReplacing(PlanarAudio input, PlanarAudio output, ReadOnlySpan<HostEvent> events);

    IReadOnlyList<PluginParameter> Parameters { get; }
    void SetParameter(int index, double normalized);       // 0..1, RT-safe scalar write
    double GetParameter(int index);

    byte[] SaveState();                                    // -> base64 into Setup JSON
    void LoadState(ReadOnlySpan<byte> state);
}
```

`HostEvent` is a format-neutral, timestamped MIDI/param event (`sampleOffset`, status, data) — the union
of VST's `deltaFrames`/`VstMidiEvent` and CLAP's `clap_event_*`. The synth already produces
timestamped events, so instrument hosting reuses that scheduling.

---

## 6. Phases

Each phase is independently shippable and gated by a **measured** acceptance test (matching the project's
A/B-with-measurement discipline — RMS/LUFS/spectral, not "trust me").

### Phase 0 — LADSPA interop spike  *(de-risk the native RT boundary)*  — ✅ VERIFIED 2026-06-19

Done and measured against real native plugins (Tom Szilagyi's TAP-plugins, built from source): scan
discovered all 19 plugins with correct UniqueIDs/ports; TAP Tremolo loaded with its exact parameter
ranges; the bridge is transparent at bypass (flat RMS 0.28277 vs input 0.28284) and full-depth tremolo
measurably drops the level (RMS 0.18164, −14.82 dBFS); the realtime path allocates zero managed bytes
per block. Self-skipping live test in `MidiSharp.Hosting.Tests` (`LadspaLiveTests`).


The whole point is to prove the hardest cross-cutting concern against the simplest ABI before CLAP.
LADSPA is one C struct of function pointers; no MIDI, no state, no GUI.

- **Loader:** `NativeLibrary.Load` + `GetExport("ladspa_descriptor")` (cross-platform API even though
  LADSPA plugins are Linux `.so`). Iterate `ladspa_descriptor(0,1,2,…)` until null to enumerate.
- **Marshal** `LADSPA_Descriptor` (PortCount, PortDescriptors[], PortNames[], PortRangeHints[]) and
  `Marshal.GetDelegateForFunctionPointer` the `instantiate / connect_port / activate / run / deactivate /
  cleanup` pointers.
- **Wire audio:** `connect_port(handle, port, ptr)` points each audio port at an unmanaged planar
  channel buffer and each **control port** at a single unmanaged float cell (the parameter value the
  plugin reads every `run`).
- **`PlanarBridge`:** deinterleave the stereo block into the input channel buffers, `run(handle,
  frames)`, reinterleave the output buffers back. Wrap as `HostedEffect : IAudioProcessor`.
- **Drop into the rack:** add the `HostedEffect` to a `ProcessorChain` and run a song through it.

**Acceptance gate:**
1. Load a known LADSPA plugin (e.g. swh `lowpass` or a simple gain), process a test tone, and confirm
   the output matches the plugin's expected transfer (measure: gain in dB / cutoff via spectrum) within
   tolerance.
2. **Zero managed allocations** per `Process` block (`GC.GetAllocatedBytesForCurrentThread()` delta == 0).
3. **Bit-identical bypass** when the chain has no hosted effect.
4. Clean teardown: `deactivate`/`cleanup`/unload with no leak across repeated load cycles.

Deliverable: `MidiSharp.Hosting` core (`PlanarBridge`, `HostedEffect`, `IHostedPlugin`, unmanaged-buffer
helpers) + `MidiSharp.Hosting.Ladspa`. Everything learned here is format-independent.

### Phase 1 — Host abstraction + CLAP effects  *(the real anchor)*  — ✅ VERIFIED 2026-06-19

`MidiSharp.Hosting.Clap` built and measured: the full CLAP ABI bound via `delegate* unmanaged[Cdecl]`
function pointers (`ClapAbi`), a minimal `clap_host` with `UnmanagedCallersOnly` callbacks (`ClapHost`),
scan/load (`ClapFormat`), and `ClapPlugin : IHostedPlugin` (activate → start_processing → planar
`clap_process`; params via the `clap.params` extension; parameter *setting* via `clap_event_param_value`
events fed through the per-block input-event list). Verified end-to-end against a real native CLAP plugin
(a stereo-gain fixture built in C from the free-audio/clap headers): gain tracks the parameter exactly —
input RMS 0.28284 → ×1 0.28277 (transparent), ×0.5 0.14139, ×2 0.56555. Reuses `PlanarBridge`/
`HostedEffect` verbatim. Self-skipping live test `ClapLiveTests`. (State save/restore deferred to Phase 2;
non-stereo main ports throw `NotSupportedException` for now.)


Promote the spike's concrete pieces into the format-agnostic abstraction (§5) and implement the first
cross-platform adapter. CLAP effects use `clap_plugin_factory` → `clap_plugin` →
`clap_plugin_audio_ports` + `clap_plugin_params`; audio via `clap_process` with `clap_audio_buffer`.

- Implement the **host side**: `clap_host` with the extensions a basic effect needs
  (`clap_host_params`, `clap_host_latency`, `log`, `thread-check`).
- Reuse `PlanarBridge` verbatim (CLAP is already planar `float**`).
- Map `clap_param_info` → `PluginParameter` (CLAP params are real-ranged with flags; normalize 0..1 for
  the UI, denormalize on `set`).

**Acceptance gate:** a real CLAP effect (e.g. Surge XT FX, or a CLAP build of a known EQ) runs as an
insert in the rack on Linux **and** the abstraction compiles/loads on Windows (CLAP is cross-platform);
measured effect on a test signal matches a reference render; invariants 1–3 hold.

### Phase 2 — Plugin discovery, registry & web UI  — ✅ VERIFIED 2026-06-19

Server-side `PluginHost` (registry of CLAP + LADSPA, scanned at startup) + endpoints `GET /api/plugins`,
`GET /api/plugin-info`, `POST /api/plugins/rescan`. `EffectRack` gained a `"plugin"` `EffectDto` variant:
it loads a `HostedEffect` via the registry, keyed by `InstanceId` so a parameter tweak reuses the loaded
native instance (no reload), with disposal deferred one Configure cycle to avoid an audio-thread
use-after-free. The web rack's add-bar lists discovered effects; selecting one renders a normalized 0..1
knob per `PluginParameter` (fetched lazily from `/api/plugin-info`). Plugin inserts persist into the
Setup JSON (format, id, instanceId, param values; + optional `clap.state` base64). CLAP state save/load
(the Phase-1 deferral) is implemented via the `clap.state` extension over GCHandle-backed streams.

Verified end-to-end against real plugins: discovery surfaced **214 effects** (195 CLAP incl. lsp-plugins/
Dragonfly/DISTRHO + 19 LADSPA); the UI rendered the gain fixture's knob from server info; the server
loaded the plugin into the master rack and reused the instance on a param change; the insert round-tripped
through save/load; and a real third-party effect (DISTRHO MaBitcrush) processes through the host
(`ClapLiveTests`). Note: loading an arbitrary untrusted plugin in-process can segfault (lsp-plugins did) —
that robustness is Phase 8 (out-of-process sandboxing).


- `PluginRegistry.Scan` over the standard per-OS search paths (`CLAP_PATH`, `~/.clap`,
  `/usr/lib/clap`, `%COMMONPROGRAMFILES%\CLAP`, `~/Library/Audio/Plug-Ins/CLAP`, …); cache results.
- New server endpoint `GET /api/plugins` (discovered effects/instruments).
- New `"plugin"` `EffectDto` variant + generic parameter UI: the existing `buildRack`/effect-strip code
  renders a knob per `PluginParameter` using its name/display/label — no plugin GUI needed.
- Persist plugin inserts (descriptor id + `SaveState()` base64 + param values) into the Setup JSON via
  the existing `SetupStore`.

**Acceptance gate:** add a discovered CLAP effect to a per-instrument rack from the browser, hear it
live, save the setup, reload it, and confirm the plugin + its state round-trip (measured: re-rendered
output matches pre-save within tolerance).

### Phase 3 — Sample-accurate events plumbing  — ✅ VERIFIED 2026-06-20

`HostEvent` generalized to carry either a MIDI message or a parameter change, each with a
`SampleOffset`. `ClapPlugin` now builds a time-ordered, heterogeneous CLAP event list per block (live UI
param sets at time 0, then the caller's events as `clap_event_param_value` / `clap_event_midi` with
`header.time = SampleOffset`) and hands it to the plugin through the input-event list.
`HostedEffect.QueueEvent` accepts per-block events and partitions them across chunks (rebasing offsets),
so the rack can feed timed automation/MIDI. Measured against the (now sample-accurate) gain fixture: a
parameter event queued at sample 256 and a MIDI CC#7 event at sample 384 each produced a gain step at
*exactly* that sample — not block-quantized. The no-event path stays allocation-free (invariant 2 still
green). This is the plumbing Phase 4 (CLAP instruments) and automation lanes build on.


Generalize `HostEvent` delivery so the host can feed timestamped MIDI/param-automation into a plugin's
process call (needed for instruments and for param automation). Reuse the synth's existing timestamped
event scheduling; split a block at event offsets or pass an event list per block (CLAP/VST3 take the
list; VST2 takes `processEvents` before `process`).

**Acceptance gate:** a param automation lane and a MIDI stream both land sample-accurately (measured:
the change occurs at the expected sample offset, not block-quantized).

### Phase 4 — CLAP instruments  — ✅ CORE VERIFIED 2026-06-20  (live player wiring = 4b, pending)

`HostedInstrument` hosts a CLAP instrument as an event-driven sound source: queue note/param events,
`Render` writes its stereo output. `ClapPlugin` gained 0-audio-input support (instruments) and mono-output
support (the mono channel is duplicated to the stereo bus). Driven by the Phase-3 timed `HostEvent` stream
(note on/off via `clap_event_midi`). Measured against the monophonic sine fixture
(`midisharp.test.synth`): silent with no notes; A4 sounds at **439.5 Hz** (want 440); a note queued at
sample 256 is strictly silent before it and sounds from exactly 256 (sample-accurate onset); note-off
silences; and the instrument's output run through a hosted **gain insert** is halved exactly (the
"inserts on top" half of the gate). Real-world: DISTRHO **Nekobi** (a mono TB-303-style synth, 8 params)
loads and sounds through the host.

**Phase 4b — live player integration — ✅ VERIFIED 2026-06-20.** Engine hooks:
`RealtimePlayer.EventDispatched` (fires each dispatched event with its block sample offset) and
`Synthesizer.MuteChannel` (a channel handed to an external source ignores NoteOn). Server: a play request
can bind a channel to a hosted plugin (`InstrumentBindingDto`); `PlayerService` loads the
`HostedInstrument`, mutes that channel on the synth, routes the channel's note/CC events to the plugin via
the dispatch hook, and renders+sums the plugin in the audio callback before the master rack. Measured
offline (`ClapPlayerIntegrationTests`): a programmatic MIDI (A4 held on channel 0) plays through the
hosted sine synth — the summed render carries 440 Hz with the synth's channel muted. Live smoke: a play
request binding channel 0 of a real MIDI to the synth fixture plays without error. **Remaining for full
productization:** a UI affordance to bind a plugin as a part's instrument (the part strip's `src` →
hosted instrument), and applying per-part gain/pan to the summed instrument (currently unity).


`HostedInstrument` feeds a mixer part/bus as an alternative to `Synthesizer`. `isInstrument` plugins get
note events via Phase 3; their stereo output sums into the part the same way synth voices do.

**Acceptance gate:** a CLAP synth plays a MIDI file through MidiSharp's mixer, with per-part gain/pan and
inserts applied on top — measured against the plugin's standalone render.

### Phase 5 — VST2 adapter

Simple C ABI (`AEffect` + `audioMasterCallback`), so it reuses all Phase 0–4 plumbing. Adds the VST2
host-callback surface (`audioMasterGetTime`, automation, idle) and `processEvents`/`deltaFrames` event
delivery. **License caveat documented**: VST2 SDK license is withdrawn — fine for personal use, gray for
distribution; the adapter ships as opt-in, no Steinberg headers vendored.

**Acceptance gate:** a known VST2 effect and a VST2 instrument both run through the rack/mixer on Linux
(`.so`) and Windows (`.dll`); measured parity with a reference host render.

### Phase 6 — VST3 adapter  *(the heavy lift)*

VST3 is a C++ COM-like ABI (`IPluginFactory`/`IComponent`/`IAudioProcessor`/`IEditController` vtables).
Two routes, evaluated at the start of this phase: (a) the official **C-API bridge**, or (b) a small
native shim compiled per-OS that exposes a **flat C surface** the C# adapter P/Invokes. Likely (b) for
control. Audio via `IAudioProcessor::process` (`ProcessData` / `AudioBusBuffers`, already planar).

**Acceptance gate:** a known VST3 effect + instrument run through MidiSharp on Win/Mac/Linux; measured
parity with a reference render.

### Phase 7 — Native plugin GUIs  *(deferred, non-web track)*

Plugin editor windows are native (X11/Win32/Cocoa) and cannot live in the browser UI. This needs a
native host window that embeds the plugin's editor (`clap_plugin_gui`, VST2 `effEditOpen`,
VST3 `IPlugView`). Separate track; parameter-only hosting (Phases 1–6) is fully usable without it.

### Phase 8 — Out-of-process sandboxing  *(stability hardening)*

Run plugins in a child process; bridge audio over a shared-memory ring and control over RPC, behind the
unchanged `IHostedPlugin` interface. A plugin crash then degrades to one dead insert, not a dead host.
Designed-for from Phase 0 (narrow boundary), implemented only when the in-process host is otherwise
solid.

### Optional Linux adapters (any time after Phase 1)

`LADSPA` (already built in Phase 0), **DSSI** (LADSPA + ALSA-`snd_seq_event_t` MIDI for instruments), and
**LV2** (the current Linux standard; RDF/Turtle metadata, more involved). Cheap once the planar/event
plumbing exists; Linux-only, so additive rather than foundational.

---

## 7. Cross-cutting concerns

- **Threading:** scan/load/activate off the audio thread; the audio callback only calls
  `ProcessReplacing`. Param edits from the UI write the unmanaged param cell (LADSPA/CLAP) or queue a
  param event (VST3); plugin swap/reorder uses `ProcessorChain.SetAll` (atomic snapshot).
- **State in the Setup:** plugin inserts persist as `{ format, descriptorId, params[], stateBase64 }` in
  the existing per-MIDI Setup JSON — same path as the EQ/limiter racks today.
- **Sample rate / block size:** activation is per (sampleRate, maxBlockFrames); re-activate on change.
  Buffers sized to `maxBlockFrames` once.
- **Latency:** plugins report latency; the host must compensate (delay-align dry/parallel paths). Track
  per insert; surface total chain latency.
- **Measurement harness:** extend the offline Demo render path so a plugin chain can be A/B'd against a
  bypass/reference render (RMS/LUFS/spectral), consistent with the project's verification style.

## 8. Risks & open questions

- **C# on the RT audio thread.** The no-GC discipline is strict; the allocation-delta test (invariant 2)
  is the guard. Worst case: pin the hot path with a server-GC / sustained-low-latency config and
  pre-warmed buffers.
- **VST3 C++ ABI.** The single biggest unknown; Phase 6 starts with a route-selection spike (C-bridge vs
  native shim) before committing.
- **VST2 licensing** for distribution — keep the adapter opt-in and header-free.
- **GUI hosting vs a web UI.** Fundamental mismatch; parameter-only is the answer until/unless a native
  window host (Phase 7) is worth it.
- **Crash safety** in-process — accepted short-term, Phase 8 is the real fix.

## 9. Status & next step

**Phase 0 is done and verified** (see above): the core (`PlanarBridge`, `HostedEffect`, `IHostedPlugin`,
unmanaged-buffer helpers) and the `MidiSharp.Hosting.Ladspa` adapter load and run real native plugins
through a `ProcessorChain`, with the loader, struct/function-pointer marshaling, interleave↔planar
kernel, no-GC hot path, and `IAudioProcessor` drop-in all validated by measurement. Every cross-cutting
concern the rest of the plan rests on is proven.

**Phases 0–4 (incl. 4b) done and verified** — LADSPA spike, CLAP effects, discovery/rack/UI/persistence,
sample-accurate events, CLAP instrument hosting, and live player integration (a MIDI file through a hosted
synth), all measured against real native plugins. **Next: Phase 5 (VST2)** then **Phase 6 (VST3)**, which
reuse the whole `PlanarBridge`/`HostedEffect`/`HostedInstrument`/event stack as ABI adapters. Smaller
follow-ups also open: the bind-a-plugin-as-instrument UI, per-part gain/pan on summed instruments, and
Phase 8 out-of-process sandboxing (the lsp-plugins in-process crash).
