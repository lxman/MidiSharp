using System;
using System.Collections.Generic;

namespace MidiSharp.SoundBank;

/// <summary>
/// One playable region. A NoteOn matches a zone iff every activation condition
/// passes; each matching zone allocates one voice. Pre-flattened from any
/// source-format hierarchy by the loader — no inheritance to resolve here.
/// </summary>
/// <remarks>
/// Optional features are nullable (filter, mod envelope, LFOs, keyswitch,
/// round-robin); null means "this feature doesn't apply to this zone." The
/// synth checks once per NoteOn — zero overhead when absent.
/// </remarks>
public sealed class PatchZone
{
    // ─── Activation conditions ──────────────────────────────────────

    public KeyRange Keys { get; init; } = new(0, 127);

    public VelocityRange Velocities { get; init; } = new(0, 127);

    /// <summary>SFZ locc/hicc gates (AND semantics). Empty for SF2/SF3/DLS.</summary>
    public IReadOnlyList<CCGate> CCGates { get; init; } = Array.Empty<CCGate>();

    /// <summary>SFZ sw_* keyswitch. Null elsewhere.</summary>
    public KeySwitch? KeySwitch { get; init; }

    /// <summary>SFZ seq_* round-robin. Null elsewhere.</summary>
    public RoundRobin? RoundRobin { get; init; }

    /// <summary>SFZ lorand/hirand random round-robin. Null elsewhere.</summary>
    public RandomRange? Random { get; init; }

    /// <summary>SF2 ExclusiveClass / SFZ group= + off_by=. Null = no grouping.</summary>
    public int? ExclusiveGroup { get; init; }

    /// <summary>
    /// SFZ note_polyphony / off_mode / off_time: when set, a voice turned off by a retrigger or an
    /// exclusive group fades out (per <see cref="OffMode"/>/<see cref="OffTimeSeconds"/>) instead of
    /// being hard-cut. False for SF2/SF3/DLS and SFZ zones that don't ask for it — those keep the
    /// abrupt kill (and byte-identical output).
    /// </summary>
    public bool SmoothVoiceOff { get; init; }

    /// <summary>SFZ off_mode: how a turned-off voice releases. Fast ≈ 6 ms; Time uses
    /// <see cref="OffTimeSeconds"/>; Normal keeps the zone's ampeg release.</summary>
    public ZoneOffMode OffMode { get; init; } = ZoneOffMode.Fast;

    /// <summary>SFZ off_time (seconds) — the fade time used when <see cref="OffMode"/> is Time.</summary>
    public double OffTimeSeconds { get; init; } = 0.006;

    /// <summary>
    /// SFZ trigger= mode: when this zone fires relative to the note. Attack (the default for every
    /// format) plays on NoteOn; Release plays on NoteOff — the damper/string-release samples a piano
    /// uses. First/Legato are NoteOn variants gated on whether another note is already sounding.
    /// SF2/SF3/DLS are always Attack.
    /// </summary>
    public ZoneTrigger Trigger { get; init; } = ZoneTrigger.Attack;

    /// <summary>
    /// SFZ rt_decay (dB per second the note was held). A release-triggered sample is attenuated by
    /// this much × the held duration, modelling a string that has already decayed while the key was
    /// down. 0 = no decay. Only meaningful when <see cref="Trigger"/> is <see cref="ZoneTrigger.Release"/>.
    /// </summary>
    public double RtDecay { get; init; }

    // ─── Humanization (SFZ; 0 = disabled, the SF2/SF3/DLS default) ──

    /// <summary>SFZ amp_random: per-note random gain, dB. A value in [0, this] (sfizz convention) is
    /// added to the note's volume at NoteOn.</summary>
    public double AmpRandomDb { get; init; }

    /// <summary>SFZ pitch_random: per-note random detune, cents. A value in [0, this] is added.</summary>
    public double PitchRandomCents { get; init; }

    /// <summary>SFZ delay: fixed time (seconds) the note is held silent before it sounds.</summary>
    public double DelaySeconds { get; init; }

    /// <summary>SFZ delay_random: per-note random extra onset delay, seconds. A value in [0, this] adds
    /// to <see cref="DelaySeconds"/>.</summary>
    public double DelayRandomSeconds { get; init; }

    /// <summary>SFZ offset_random: per-note random extra sample-start offset, frames. A value in
    /// [0, this] adds to the region's fixed start offset.</summary>
    public long OffsetRandomFrames { get; init; }

    // ─── Sample reference ───────────────────────────────────────────

    public SampleRef Sample { get; init; } = new();

    // ─── Static playback parameters ─────────────────────────────────

    public PitchSettings Pitch { get; init; } = new();

    public LevelSettings Level { get; init; } = new();

    // ─── Time-varying modulators ────────────────────────────────────

    /// <summary>Always present; the volume envelope is non-optional.</summary>
    public EnvelopeSettings VolumeEnvelope { get; init; } = new() { SustainLevel = 1.0 };

    public EnvelopeSettings? ModulationEnvelope { get; init; }

    public LFOSettings? VibratoLFO { get; init; }

    public LFOSettings? ModulationLFO { get; init; }

    public FilterSettings? Filter { get; init; }

    /// <summary>
    /// SFZ optional second filter (cutoff2/fil2_type/resonance2), cascaded in series after
    /// <see cref="Filter"/>. Null when the zone sets none (SF2/DLS and most SFZ).
    /// </summary>
    public FilterSettings? Filter2 { get; init; }

    /// <summary>Live CC modulation of the second filter's cutoff (cutoff2_cc{N}), in cents. Null = none.</summary>
    public LfoCcDepth[]? Filter2CutoffCc { get; init; }

    /// <summary>SFZ peaking-EQ bands (eqN_freq/bw/gain). Empty for SF2/SF3/DLS and SFZ without EQ.</summary>
    public IReadOnlyList<EqBand> EqBands { get; init; } = Array.Empty<EqBand>();

    // ─── Sends (0..1) ───────────────────────────────────────────────

    public double ReverbSend { get; init; }

    public double ChorusSend { get; init; }

    // ─── Routing matrix ─────────────────────────────────────────────

    public IReadOnlyList<ModulationRoute> Routes { get; init; } = Array.Empty<ModulationRoute>();

    /// <summary>
    /// SFZ amp_velcurve_N: a 128-entry velocity→gain table (index = velocity 0..127,
    /// value = linear gain 0..1). When present it replaces the default velocity→
    /// attenuation route for this zone. Null elsewhere (SF2/SF3/DLS and SFZ zones
    /// without a custom curve, which use the velocity modulation route instead).
    /// </summary>
    public double[]? AmpVelCurve { get; init; }

    /// <summary>
    /// SFZ keyboard crossfade (xfin/xfout_lokey/hikey): a 128-entry key→gain table
    /// (index = note number 0..127, value = linear gain 0..1) applied as a static per-note
    /// factor at NoteOn, alongside <see cref="AmpVelCurve"/>. Null when the zone sets no key
    /// crossfade (the common case).
    /// </summary>
    public double[]? AmpKeyCurve { get; init; }

    /// <summary>
    /// SFZ live CC crossfades (xfin/xfout_locc{N}/hicc{N}): each entry morphs the zone's gain
    /// by the current value of its controller, re-read every block — this is the mod-wheel layer
    /// fade. Null when the zone sets no CC crossfade.
    /// </summary>
    public CcCrossfade[]? CcCrossfades { get; init; }

    /// <summary>
    /// SFZ stereo width (the <c>width</c> opcode) as a normalized factor: 1.0 = full stereo (default),
    /// 0.0 = mono (mid only), -1.0 = channels swapped. Applied as a mid/side scale to stereo samples
    /// only; a no-op (1.0) for mono samples and for zones that don't set <c>width</c>.
    /// </summary>
    public double WidthNormalized { get; init; } = 1.0;

    /// <summary>Live CC modulation of stereo width (width_oncc{N}), in width-percent. Null = none.</summary>
    public LfoCcDepth[]? WidthCc { get; init; }

    /// <summary>
    /// SFZ note_selfmask: when true (default), retriggering the same key cuts the previous voice;
    /// note_selfmask=off lets overlapping same-key notes ring together (plucked/struck instruments).
    /// </summary>
    public bool NoteSelfMask { get; init; } = true;

    /// <summary>SFZ bend_smooth: time constant (seconds) to glide pitch-bend changes. 0 = instant.</summary>
    public double BendSmoothSeconds { get; init; }

    /// <summary>SFZ amp_keytrack: gain change in dB per key away from <see cref="AmpKeyTrackCenter"/>. 0 = none.</summary>
    public double AmpKeyTrackDbPerKey { get; init; }

    /// <summary>SFZ amp_keycenter: reference key for <see cref="AmpKeyTrackDbPerKey"/> (default 60).</summary>
    public int AmpKeyTrackCenter { get; init; } = 60;

    /// <summary>SFZ pan_keytrack: pan change (in pan-%, -100..100) per key from <see cref="PanKeyTrackCenter"/>.</summary>
    public double PanKeyTrackPercentPerKey { get; init; }

    /// <summary>SFZ pan_keycenter: reference key for <see cref="PanKeyTrackPercentPerKey"/> (default 60).</summary>
    public int PanKeyTrackCenter { get; init; } = 60;

    /// <summary>SFZ fil_random: max random per-note filter cutoff offset, in cents. 0 = none.</summary>
    public double FilterRandomCents { get; init; }

    /// <summary>SFZ pitchlfo_freq_oncc{N}: CCs that add to the vibrato-LFO frequency (Hz). Null = none.</summary>
    public LfoCcDepth[]? VibLfoFreqCc { get; init; }

    /// <summary>
    /// SFZ v2 generic LFOs (<c>lfoN_*</c>): indexed oscillators routed to pitch/volume/cutoff/etc.,
    /// run per-sample in the voice alongside the SF2 two-slot LFOs. Null for SF2/DLS and for SFZ zones
    /// that declare none (the common case), which keeps those paths untouched.
    /// </summary>
    public GenericLfo[]? Lfos { get; init; }

    /// <summary>
    /// SFZ v2 generic flex envelopes (<c>egN_*</c>): multi-segment envelopes routed to pitch/cutoff/
    /// volume, run per-sample in the voice. Null for SF2/DLS and SFZ zones that declare none.
    /// </summary>
    public GenericEg[]? Egs { get; init; }

    /// <summary>SFZ pitch_veltrack: cents added to the note's pitch at full velocity, scaled linearly by
    /// velocity (sfizz: cents × vel/127). 0 = none (the default for every format).</summary>
    public double PitchVelTrackCents { get; init; }

    /// <summary>SFZ offset_oncc{N}: CCs that add to the sample start offset (in frames), baked once at
    /// note-on from the live controller. Null = none.</summary>
    public LfoCcDepth[]? OffsetCc { get; init; }

    /// <summary>SFZ polyphony: maximum simultaneous voices this region may sound; past the cap the oldest
    /// is stolen. -1 = unlimited (the default for every format and most SFZ regions).</summary>
    public int Polyphony { get; init; } = -1;

    /// <summary>SFZ sustain_cc: the MIDI controller this region treats as the sustain pedal. Default CC64;
    /// half-pedal SFZ fonts reassign it (e.g. to CC90, freeing CC64 to modulate the release envelope).</summary>
    public int SustainCc { get; init; } = 64;
}

/// <summary>
/// One live CC crossfade: the zone's gain is multiplied by <see cref="Gain"/>[controller value]
/// each block, so sweeping controller <see cref="Cc"/> fades the layer in/out. The 128-entry table
/// is precomputed by the loader from the xfin/xfout thresholds and xf_cccurve shape.
/// </summary>
public readonly struct CcCrossfade(int cc, double[] gain)
{
    /// <summary>The MIDI controller number driving this crossfade.</summary>
    public int Cc { get; } = cc;

    /// <summary>128-entry linear-gain table indexed by the controller's current value (0..127).</summary>
    public double[] Gain { get; } = gain;
}

/// <summary>SFZ <c>trigger=</c> mode — when a zone fires relative to the note event.</summary>
public enum ZoneTrigger
{
    /// <summary>Fire on NoteOn (the default for every source format).</summary>
    Attack,

    /// <summary>Fire on NoteOff — damper / string-release samples.</summary>
    Release,

    /// <summary>Fire on NoteOn only if no other note is already sounding on the channel.</summary>
    First,

    /// <summary>Fire on NoteOn only if another note is already sounding on the channel (legato).</summary>
    Legato,
}

/// <summary>SFZ <c>off_mode=</c> — how a voice releases when turned off by a retrigger or off_by group.</summary>
public enum ZoneOffMode
{
    /// <summary>Quick ~6 ms fade (the SFZ default).</summary>
    Fast,

    /// <summary>Use the zone's normal ampeg release time.</summary>
    Normal,

    /// <summary>Fade over the zone's <c>off_time</c> seconds.</summary>
    Time,
}
