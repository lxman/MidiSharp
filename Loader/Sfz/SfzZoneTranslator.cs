using System;
using System.Collections.Generic;
using MidiSharp.Audio;
using MidiSharp.SoundBank;

namespace Loader.Sfz;

/// <summary>
/// Translates one flattened <see cref="SfzRegion"/> into an IR
/// <see cref="PatchZone"/>. Opcode values are already in domain-natural units
/// in SFZ (seconds, Hz, dB, cents, percent) — much closer to the IR than SF2's
/// timecent/centibel encodings — so the conversions here are mostly direct.
/// </summary>
internal static class SfzZoneTranslator
{
    /// <summary>SFZ default pitch_keycenter when neither it nor <c>key</c> is given.</summary>
    private const int DefaultKeyCenter = 60;

    public static PatchZone Build(SfzRegion r, SfzControl control, int sampleId, AudioInfo wav)
    {
        // ── Key / velocity activation ────────────────────────────────
        int? keyAll = r.GetKey("key", control);
        int lokey = r.GetKey("lokey", control) ?? keyAll ?? 0;
        int hikey = r.GetKey("hikey", control) ?? keyAll ?? 127;
        int rootKey = r.GetKey("pitch_keycenter", control) ?? keyAll
                      ?? Math.Clamp(DefaultKeyCenter + control.KeyOffset, 0, 127);

        int lovel = r.GetInt("lovel", 0);
        int hivel = r.GetInt("hivel", 127);

        // ── CC gates (locc/hicc, on_locc/on_hicc) ────────────────────
        var ccGates = BuildCcGates(r);

        // ── Tuning ───────────────────────────────────────────────────
        // tune (a.k.a. pitch) is cents; transpose is semitones. group_tune adds a group-level cents
        // offset on top (the region inherits it through the header hierarchy).
        double tuneCents = r.GetDouble("tune", r.GetDouble("pitch", 0)) + r.GetDouble("group_tune", 0);
        double transpose = r.GetDouble("transpose", 0);

        // ── Level / pan ──────────────────────────────────────────────
        // SFZ volume is signed dB, positive = louder; the *_volume variants at
        // each hierarchy level sum with it. IR AttenuationDb is the negation.
        double totalVolumeDb = r.GetDouble("volume", 0)
                             + r.GetDouble("global_volume", 0)
                             + r.GetDouble("master_volume", 0)
                             + r.GetDouble("group_volume", 0);

        // amplitude is a linear-% gain (100 = unchanged) that multiplies on top of volume — so it
        // adds as dB here, and combines correctly with the amplitude_oncc route (also dB). 0 → silence
        // (floored so the log is finite). 100 is the default and a no-op, keeping non-amplitude zones
        // byte-identical.
        double amplitude = r.GetDouble("amplitude", 100.0);
        if (amplitude != 100.0)
            totalVolumeDb += 20.0 * Math.Log10(Math.Max(amplitude, 1e-4) / 100.0);

        double pan = Math.Clamp(r.GetDouble("pan", 0) / 100.0, -1.0, 1.0);

        // ── Amp envelope (DAHDSR; sustain is percent) ────────────────
        // ampeg_{stage}_oncc{N}: a CC shifts the stage (seconds, or percent for sustain), shaped by the
        // curve and evaluated at the seeded initial CC — baked in here like amp_veltrack_oncc. This is
        // what gives Salamander its ~1 s damper release (ampeg_release_oncc72=2, cc72 seeded to 0.5).
        var ic = control.InitialControllers;
        // ampeg_dynamic=1 re-reads the CC-modulated envelope stages from the LIVE controller (the voice
        // evaluates CcMods at note-on). Without it, we keep the current behaviour: bake the CC offset at
        // the static seeded CC here — which is correct for the static-seed case and keeps every existing
        // font byte-identical (the dynamic path is opt-in, gated on this flag).
        bool dynamic = r.GetInt("ampeg_dynamic", 0) != 0;
        double CcBake(string stage) => dynamic ? 0.0 : EnvCcOffset(r, ic, stage, control.Curves);
        var ampEnv = new EnvelopeSettings
        {
            DelaySeconds = r.GetDouble("ampeg_delay", 0) + CcBake("ampeg_delay"),
            AttackSeconds = r.GetDouble("ampeg_attack", 0) + CcBake("ampeg_attack"),
            HoldSeconds = r.GetDouble("ampeg_hold", 0) + CcBake("ampeg_hold"),
            DecaySeconds = r.GetDouble("ampeg_decay", 0) + CcBake("ampeg_decay"),
            SustainLevel = Math.Clamp((r.GetDouble("ampeg_sustain", 100.0) + CcBake("ampeg_sustain")) / 100.0, 0, 1),
            // Floor the release so a NoteOff mid-sample fades instead of clicking.
            ReleaseSeconds = Math.Max(r.GetDouble("ampeg_release", 0) + CcBake("ampeg_release"), 0.003),
            // Velocity → envelope (ampeg_vel2*): times in seconds, sustain as a 0..1 offset.
            VelToDelaySeconds = r.GetDouble("ampeg_vel2delay", 0),
            VelToAttackSeconds = r.GetDouble("ampeg_vel2attack", 0),
            VelToHoldSeconds = r.GetDouble("ampeg_vel2hold", 0),
            VelToDecaySeconds = r.GetDouble("ampeg_vel2decay", 0),
            VelToReleaseSeconds = r.GetDouble("ampeg_vel2release", 0),
            VelToSustainLevel = r.GetDouble("ampeg_vel2sustain", 0) / 100.0,
            Dynamic = dynamic,
            CcMods = dynamic ? CollectEnvCcMods(r) : null,
        };

        // ── Filter ───────────────────────────────────────────────────
        FilterSettings? filter = BuildFilter(r, out double fileDepthCents);
        FilterSettings? filter2 = BuildSecondFilter(r, out var filter2CutoffCc);
        // Generic LFOs are built first so EQ bands they target (lfoN_eqNgain/freq) get instantiated
        // even when their static gain is 0 (the LFO oscillates around it).
        var lfos = BuildGenericLfos(r);
        var lfoEqBands = CollectLfoEqBands(lfos);
        var eqBands = BuildEqBands(r, lfoEqBands);

        // ── Modulation envelope (filter and/or pitch EG) ─────────────
        double pitchEgDepth = r.GetDouble("pitcheg_depth", 0);
        EnvelopeSettings? modEnv = null;
        double pitchModEnvCents = 0;
        if (fileDepthCents != 0 || pitchEgDepth != 0)
        {
            // One IR mod-envelope serves both filter and pitch EG. Prefer the
            // filter EG's timing when present (it's the more common SFZ use).
            string p = fileDepthCents != 0 ? "fileg_" : "pitcheg_";
            modEnv = new EnvelopeSettings
            {
                DelaySeconds = r.GetDouble(p + "delay", 0),
                AttackSeconds = r.GetDouble(p + "attack", 0),
                HoldSeconds = r.GetDouble(p + "hold", 0),
                DecaySeconds = r.GetDouble(p + "decay", 0),
                SustainLevel = Math.Clamp(r.GetDouble(p + "sustain", 100.0) / 100.0, 0, 1),
                ReleaseSeconds = r.GetDouble(p + "release", 0),
            };
            pitchModEnvCents = pitchEgDepth;
        }

        // ── LFOs ─────────────────────────────────────────────────────
        LFOSettings? vibratoLfo = BuildPitchLfo(r);
        LFOSettings? modLfo = BuildModLfo(r);

        // ── Looping + sample addressing ──────────────────────────────
        var (loopMode, loopStart, loopEnd) = ResolveLoop(r, wav);
        long? startOffset = r.Has("offset") ? r.GetInt("offset", 0) : (long?)null;
        long? endOffset = r.Has("end") ? r.GetInt("end", 0) : (long?)null;

        var sampleRef = new SampleRef
        {
            SampleId = sampleId,
            LoopMode = loopMode,
            OverridingRootKey = rootKey,
            FineTuneCents = tuneCents,
            CoarseTuneSemitones = transpose,
            ScaleTuningCentsPerKey = r.GetDouble("pitch_keytrack", 100.0),
            StartOffset = startOffset,
            EndOffset = endOffset,
            LoopStartOffset = loopStart,
            LoopEndOffset = loopEnd,
        };

        // ── Velocity → gain ──────────────────────────────────────────
        // An explicit amp_velcurve_N table maps velocity→gain directly. Otherwise synthesize the curve
        // from the effective amp_veltrack using sfizz's law (gain = 1 - |vt|·(1 - vel²)), so SFZ
        // dynamics match the reference instead of our older dB-attenuation route. Either way velocity
        // is a per-note linear factor applied in Voice — no separate velocity modulation route.
        double[] ampVelCurve = BuildVelocityCurve(r)
                               ?? BuildVeltrackCurve(ComputeEffectiveVeltrack(r, control.InitialControllers, control.Curves));
        // Velocity crossfade (xfin/xfout) folds in as an extra velocity→gain factor, so layers fade
        // across their boundaries instead of switching abruptly.
        ApplyVelocityCrossfade(r, ampVelCurve);

        // ── Routes: _oncc modulations + suppressed default-CC routes ──
        var routes = BuildRoutes(r, control.InitialControllers, control.Curves);

        // ── Round-robin (seq_length / seq_position) ──────────────────
        RoundRobin? roundRobin = null;
        if (r.Has("seq_length") || r.Has("seq_position"))
        {
            int len = Math.Max(1, r.GetInt("seq_length", 1));
            int pos = Math.Clamp(r.GetInt("seq_position", 1), 1, len);
            roundRobin = new RoundRobin(pos - 1, len);
        }

        // ── Random round-robin (lorand / hirand) ─────────────────────
        RandomRange? random = null;
        if (r.Has("lorand") || r.Has("hirand"))
            random = new RandomRange(r.GetDouble("lorand", 0.0), r.GetDouble("hirand", 1.0));

        // ── Keyswitch (sw_last / sw_lokey / sw_hikey / sw_default) ────
        KeySwitch? keySwitch = null;
        int? swSelecting = r.GetKey("sw_last", control) ?? r.GetKey("sw_down", control);
        if (swSelecting.HasValue)
        {
            int swLo = r.GetKey("sw_lokey", control) ?? swSelecting.Value;
            int swHi = r.GetKey("sw_hikey", control) ?? swSelecting.Value;
            int swDefault = r.GetKey("sw_default", control) ?? swLo;
            keySwitch = new KeySwitch((byte)swLo, (byte)swHi, (byte)swSelecting.Value, (byte)swDefault);
        }

        // ── Exclusive group (group / off_by, a.k.a. offby) ───────────
        int? exclusive = null;
        if (r.Has("group")) exclusive = r.GetInt("group", 0);
        else if (r.Has("off_by")) exclusive = r.GetInt("off_by", 0);
        else if (r.Has("offby")) exclusive = r.GetInt("offby", 0);   // no-underscore alias
        if (exclusive is 0) exclusive = null;

        // ── Trigger mode + rt_decay ──────────────────────────────────
        // trigger= decides whether a zone fires on NoteOn (attack, the default) or NoteOff (release —
        // damper/string-release samples). rt_decay attenuates a release sample by N dB per second the
        // note was held (a string that already decayed under the key).
        ZoneTrigger trigger = ParseTrigger(r.Get("trigger"));
        double rtDecay = r.GetDouble("rt_decay", 0);

        // ── Voice-off (note_polyphony / off_mode / off_time) ─────────
        // Any of these opts the zone into smooth voice-off (fade on retrigger / group-kill). off_time
        // implies off_mode=time when off_mode isn't given (sfizz's rule); off_time defaults to 6 ms.
        bool smoothOff = r.Has("note_polyphony") || r.Has("off_mode") || r.Has("off_time");
        ZoneOffMode offMode = ParseOffMode(r.Get("off_mode"), r.Has("off_time"));
        double offTime = r.GetDouble("off_time", 0.006);

        // ── Humanization (amp/pitch/delay/offset random + fixed delay) ─
        double ampRandomDb = r.GetDouble("amp_random", 0);
        double pitchRandomCents = r.GetDouble("pitch_random", 0);
        // delay_cc{N} / delay_oncc{N} shift the start delay by a CC, evaluated once at the seeded CC
        // (the delay is fixed at note onset). Negative offsets can cancel a base delay → clamp at 0.
        double delaySeconds = Math.Max(0, r.GetDouble("delay", 0) + EnvCcOffset(r, ic, "delay", control.Curves));
        double delayRandomSeconds = r.GetDouble("delay_random", 0);
        long offsetRandomFrames = r.Has("offset_random") ? r.GetInt("offset_random", 0) : 0;

        return new PatchZone
        {
            Keys = new KeyRange((byte)Math.Clamp(lokey, 0, 127), (byte)Math.Clamp(hikey, 0, 127)),
            Velocities = new VelocityRange((byte)Math.Clamp(lovel, 0, 127), (byte)Math.Clamp(hivel, 0, 127)),
            CCGates = ccGates,
            RoundRobin = roundRobin,
            Random = random,
            KeySwitch = keySwitch,
            ExclusiveGroup = exclusive,
            Trigger = trigger,
            RtDecay = rtDecay,
            SmoothVoiceOff = smoothOff,
            OffMode = offMode,
            OffTimeSeconds = offTime,
            AmpRandomDb = ampRandomDb,
            PitchRandomCents = pitchRandomCents,
            DelaySeconds = delaySeconds,
            DelayRandomSeconds = delayRandomSeconds,
            OffsetRandomFrames = offsetRandomFrames,

            Sample = sampleRef,
            Pitch = new PitchSettings { ModulationEnvelopeDepthCents = pitchModEnvCents },
            Level = new LevelSettings { AttenuationDb = -totalVolumeDb, Pan = pan },

            VolumeEnvelope = ampEnv,
            ModulationEnvelope = modEnv,
            VibratoLFO = vibratoLfo,
            ModulationLFO = modLfo,
            Filter = filter,
            Filter2 = filter2,
            Filter2CutoffCc = filter2CutoffCc,
            EqBands = eqBands,

            ReverbSend = Math.Clamp(r.GetDouble("effect1", 0) / 100.0, 0, 1),
            ChorusSend = Math.Clamp(r.GetDouble("effect2", 0) / 100.0, 0, 1),

            Routes = routes,
            AmpVelCurve = ampVelCurve,
            AmpKeyCurve = BuildKeyCrossfade(r, control),
            CcCrossfades = BuildCcCrossfades(r),
            WidthNormalized = r.GetDouble("width", 100.0) / 100.0,
            WidthCc = CollectCcAmounts(r, "width_oncc", "width_cc"),
            NoteSelfMask = !string.Equals(r.Get("note_selfmask")?.Trim(), "off", StringComparison.OrdinalIgnoreCase),
            BendSmoothSeconds = r.GetDouble("bend_smooth", 0) / 1000.0,   // SFZ bend_smooth is milliseconds
            AmpKeyTrackDbPerKey = r.GetDouble("amp_keytrack", 0),
            AmpKeyTrackCenter = r.GetKey("amp_keycenter", control) ?? 60,
            PanKeyTrackPercentPerKey = r.GetDouble("pan_keytrack", 0),
            PanKeyTrackCenter = r.GetKey("pan_keycenter", control) ?? 60,
            FilterRandomCents = r.GetDouble("fil_random", 0),
            VibLfoFreqCc = CollectCcAmounts(r, "pitchlfo_freq_oncc", "pitchlfo_freq_cc"),
            Lfos = lfos,
            Egs = BuildGenericEgs(r),
        };
    }

    /// <summary>
    /// Maps the SFZ <c>trigger=</c> value to a <see cref="ZoneTrigger"/>. ARIA's <c>release_key</c>
    /// (release that ignores the sustain pedal) folds into Release for now; anything unrecognised —
    /// including a missing opcode or the literal <c>attack</c> — is the default Attack.
    /// </summary>
    private static ZoneTrigger ParseTrigger(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "release" or "release_key" => ZoneTrigger.Release,
        "first" => ZoneTrigger.First,
        "legato" => ZoneTrigger.Legato,
        _ => ZoneTrigger.Attack,
    };

    /// <summary>
    /// Sums a parameter's CC modulation ({param}_oncc{N} / {param}_cc{N}, plus the v1 bare-cc
    /// {param}cc{N} alias) evaluated at the seeded initial CC through its curve ({param}_curvecc{N}),
    /// matching sfizz. Used for amp-envelope stages (ampeg_{stage}) and the region start delay
    /// (delay), both fixed at note onset. Returns the additive offset in the param's units (seconds,
    /// or percent for sustain); 0 when no CC is seeded.
    /// </summary>
    private static double EnvCcOffset(SfzRegion r, IReadOnlyDictionary<int, int> initialCc, string stageParam,
        IReadOnlyDictionary<int, double[]> curves)
    {
        double sum = 0;
        foreach (var (param, cc, value) in r.EnumerateModulations())
        {
            if (param != stageParam || !initialCc.TryGetValue(cc, out int ccVal)) continue;
            int curveIdx = r.GetInt(stageParam + "_curvecc" + cc, 0);
            sum += value * EvalCurve(curveIdx, ccVal / 127.0, curves);
        }

        // v1/ARIA short form ampeg_{stage}cc{N} (no underscore) — an alias of ampeg_{stage}_oncc{N}
        // that EnumerateModulations doesn't surface (it only matches the _oncc/_cc spellings). The
        // "{stage}cc" prefix can't collide with "{stage}_oncc" or "{stage}_curvecc", so fold it in here.
        foreach (var (cc, raw) in r.EnumerateCc(stageParam + "cc"))
        {
            if (!initialCc.TryGetValue(cc, out int ccVal)) continue;
            if (!double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value)) continue;
            int curveIdx = r.GetInt(stageParam + "_curvecc" + cc, 0);
            sum += value * EvalCurve(curveIdx, ccVal / 127.0, curves);
        }
        return sum;
    }

    /// <summary>
    /// Collects every envelope-stage CC modulation (ampeg_{stage}_oncc{N} / _cc{N} / the bare-cc alias,
    /// with its curve) as <see cref="EnvCcMod"/>s for the voice to evaluate live — used only on
    /// ampeg_dynamic zones. Mirrors <see cref="EnvCcOffset"/>'s matching but keeps the raw terms instead
    /// of summing at a fixed CC. Returns null when no stage is CC-modulated.
    /// </summary>
    private static EnvCcMod[]? CollectEnvCcMods(SfzRegion r)
    {
        List<EnvCcMod>? list = null;
        void Collect(string stageParam, EnvStage stage)
        {
            foreach (var (param, cc, value) in r.EnumerateModulations())
            {
                if (param != stageParam) continue;
                int curve = r.GetInt(stageParam + "_curvecc" + cc, 0);
                (list ??= new List<EnvCcMod>()).Add(new EnvCcMod(stage, cc, value, curve));
            }
            foreach (var (cc, raw) in r.EnumerateCc(stageParam + "cc"))
            {
                if (!double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double value)) continue;
                int curve = r.GetInt(stageParam + "_curvecc" + cc, 0);
                (list ??= new List<EnvCcMod>()).Add(new EnvCcMod(stage, cc, value, curve));
            }
        }
        Collect("ampeg_delay", EnvStage.Delay);
        Collect("ampeg_attack", EnvStage.Attack);
        Collect("ampeg_hold", EnvStage.Hold);
        Collect("ampeg_decay", EnvStage.Decay);
        Collect("ampeg_sustain", EnvStage.Sustain);
        Collect("ampeg_release", EnvStage.Release);
        return list?.ToArray();
    }

    /// <summary>
    /// Evaluates an ARIA built-in CC curve at normalised input x∈[0,1] (matches sfizz's predefined
    /// curves 0–6). Curves are linear ramps (0–3) and quadratic/sqrt shapes (4–6); unknown → linear.
    /// Custom &lt;curve&gt; definitions aren't modelled yet — they fall back to linear here.
    /// </summary>
    private static double EvalBuiltinCurve(int index, double x) => AriaCurve.Eval(index, x);

    /// <summary>
    /// Maps SFZ <c>off_mode=</c> to <see cref="ZoneOffMode"/>. When unspecified, the presence of an
    /// <c>off_time</c> implies Time (sfizz's behaviour); otherwise the default is Fast.
    /// </summary>
    private static ZoneOffMode ParseOffMode(string? value, bool hasOffTime) => value?.Trim().ToLowerInvariant() switch
    {
        "time" => ZoneOffMode.Time,
        "normal" => ZoneOffMode.Normal,
        "fast" => ZoneOffMode.Fast,
        _ => hasOffTime ? ZoneOffMode.Time : ZoneOffMode.Fast,
    };

    /// <summary>
    /// Builds a 128-entry velocity→gain table from <c>amp_velcurve_N</c> points, or null
    /// when the region defines none. Defined velocities set their gain exactly; gaps are
    /// linearly interpolated. The ends are anchored (velocity 0 → 0, velocity 127 → 1)
    /// unless the region overrides them, matching ARIA's default curve endpoints.
    /// </summary>
    private static double[]? BuildVelocityCurve(SfzRegion r)
    {
        // amp_velcurve_{N} = gain — reuse the prefix enumerator (parses the trailing N).
        var points = new SortedDictionary<int, double>();
        foreach (var (vel, value) in r.EnumerateCc("amp_velcurve_"))
        {
            if (vel < 0 || vel > 127) continue;
            if (double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double g))
                points[vel] = Math.Max(0.0, g);
        }
        if (points.Count == 0) return null;

        if (!points.ContainsKey(0)) points[0] = 0.0;
        if (!points.ContainsKey(127)) points[127] = 1.0;

        var curve = new double[128];
        var keys = new List<int>(points.Keys);
        for (int i = 0; i < keys.Count - 1; i++)
        {
            int v0 = keys[i], v1 = keys[i + 1];
            double g0 = points[v0], g1 = points[v1];
            for (int v = v0; v <= v1; v++)
            {
                double t = v1 == v0 ? 0.0 : (double)(v - v0) / (v1 - v0);
                curve[v] = g0 + (g1 - g0) * t;
            }
        }
        // Flat-fill anything below the lowest / above the highest defined point.
        for (int v = 0; v < keys[0]; v++) curve[v] = points[keys[0]];
        for (int v = keys[keys.Count - 1] + 1; v < 128; v++) curve[v] = points[keys[keys.Count - 1]];
        return curve;
    }

    private static IReadOnlyList<CCGate> BuildCcGates(SfzRegion r)
    {
        // Collect the union of CC numbers mentioned by any gate opcode, then
        // build one gate per CC from its lo/hi (defaulting to the full range).
        var los = new Dictionary<int, int>();
        var his = new Dictionary<int, int>();
        foreach (var (cc, v) in r.EnumerateCc("locc")) los[cc] = ParseByte(v, 0);
        foreach (var (cc, v) in r.EnumerateCc("on_locc")) los[cc] = ParseByte(v, 0);
        foreach (var (cc, v) in r.EnumerateCc("hicc")) his[cc] = ParseByte(v, 127);
        foreach (var (cc, v) in r.EnumerateCc("on_hicc")) his[cc] = ParseByte(v, 127);

        if (los.Count == 0 && his.Count == 0) return Array.Empty<CCGate>();

        var ccs = new HashSet<int>(los.Keys);
        ccs.UnionWith(his.Keys);
        var gates = new List<CCGate>(ccs.Count);
        foreach (int cc in ccs)
        {
            int lo = los.TryGetValue(cc, out var l) ? l : 0;
            int hi = his.TryGetValue(cc, out var h) ? h : 127;
            gates.Add(new CCGate((byte)cc, (byte)Math.Clamp(lo, 0, 127), (byte)Math.Clamp(hi, 0, 127)));
        }
        return gates;
    }

    /// <summary>
    /// Collects SFZ peaking-EQ bands (eqN_freq/bw/gain). A band with 0 dB gain or no positive
    /// frequency is inactive and skipped. Probes N=1..8 (banks rarely use more than 3).
    /// </summary>
    /// <summary>Band numbers an LFO modulates (lfoN_eqNgain/freq) — those bands must exist even at gain 0.</summary>
    private static HashSet<int>? CollectLfoEqBands(GenericLfo[]? lfos)
    {
        if (lfos == null) return null;
        HashSet<int>? set = null;
        foreach (var lfo in lfos)
            foreach (var t in lfo.Targets)
                if (t.Destination is LfoDestination.EqGain or LfoDestination.EqFreq)
                    (set ??= new HashSet<int>()).Add(t.EqBand);
        return set;
    }

    private static IReadOnlyList<EqBand> BuildEqBands(SfzRegion r, HashSet<int>? lfoBands)
    {
        List<EqBand>? bands = null;
        for (int n = 1; n <= 8; n++)
        {
            double gain = r.GetDouble("eq" + n + "_gain", 0);
            double freq = r.GetDouble("eq" + n + "_freq", 0);
            double velGain = r.GetDouble("eq" + n + "_vel2gain", 0);
            // Keep a band if it has audible static gain, an LFO drives it, or velocity drives it (then a
            // 0-gain band is the centre that's modulated). Either way it needs a defined frequency.
            bool lfoDriven = lfoBands?.Contains(n) == true;
            if ((gain == 0 && !lfoDriven && velGain == 0) || freq <= 0) continue;
            double bw = r.GetDouble("eq" + n + "_bw", 1.0);
            (bands ??= new List<EqBand>()).Add(new EqBand
            {
                FrequencyHz = freq,
                BandwidthOctaves = bw,
                GainDb = gain,
                BandNumber = n,
                VelToGainDb = velGain,
            });
        }
        return bands ?? (IReadOnlyList<EqBand>)Array.Empty<EqBand>();
    }

    private static FilterSettings? BuildFilter(SfzRegion r, out double envDepthCents)
    {
        envDepthCents = r.GetDouble("fileg_depth", 0);
        bool hasCutoff = r.Has("cutoff");
        if (!hasCutoff && envDepthCents == 0) return null;

        double cutoff = r.GetDouble("cutoff", 20000.0);
        return new FilterSettings
        {
            Type = ParseFilterType(r.Get("fil_type")),
            CutoffHz = cutoff,
            ResonanceDb = r.GetDouble("resonance", 0),
            KeyTrackCentsPerKey = r.GetDouble("fil_keytrack", 0),
            KeyTrackCenter = r.GetInt("fil_keycenter", 60),
            EnvelopeDepthCents = envDepthCents,
            EnvVelToDepthCents = r.GetDouble("fileg_vel2depth", 0),
            LfoDepthCents = r.GetDouble("fillfo_depth", 0),
        };
    }

    /// <summary>Collects {prefix}{N}=amount CC modulations (e.g. width_oncc31) into (cc, amount) pairs.</summary>
    private static LfoCcDepth[]? CollectCcAmounts(SfzRegion r, params string[] prefixes)
    {
        List<LfoCcDepth>? list = null;
        foreach (var prefix in prefixes)
            foreach (var (n, raw) in r.EnumerateCc(prefix))
                if (double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double amt))
                    (list ??= new List<LfoCcDepth>()).Add(new LfoCcDepth(n, amt));
        return list?.ToArray();
    }

    private static FilterType ParseFilterType(string? filType) =>
        (filType ?? "lpf_2p").ToLowerInvariant() switch
        {
            var s when s.StartsWith("hpf") => FilterType.HighPass,
            var s when s.StartsWith("bpf") => FilterType.BandPass,
            var s when s.StartsWith("brf") => FilterType.Notch,
            var s when s.StartsWith("lsh") => FilterType.LowShelf,
            var s when s.StartsWith("hsh") => FilterType.HighShelf,
            _ => FilterType.LowPass,
        };

    /// <summary>
    /// Parses the optional SFZ second filter (cutoff2 / fil2_type / resonance2), cascaded in series
    /// after the first. Its live cutoff modulation (cutoff2_cc{N}) is returned separately for the voice
    /// to apply per block. Returns null when the region sets no second filter.
    /// </summary>
    private static FilterSettings? BuildSecondFilter(SfzRegion r, out LfoCcDepth[]? cutoffCc)
    {
        cutoffCc = null;
        if (!r.Has("cutoff2") && !r.Has("fil2_type")) return null;

        List<LfoCcDepth>? cc = null;
        foreach (var prefix in new[] { "cutoff2_oncc", "cutoff2_cc" })
            foreach (var (n, raw) in r.EnumerateCc(prefix))
                if (double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double cents))
                    (cc ??= new List<LfoCcDepth>()).Add(new LfoCcDepth(n, cents));
        cutoffCc = cc?.ToArray();

        return new FilterSettings
        {
            Type = ParseFilterType(r.Get("fil2_type")),
            CutoffHz = r.GetDouble("cutoff2", 20000.0),
            ResonanceDb = r.GetDouble("resonance2", 0),
        };
    }

    private static LFOSettings? BuildPitchLfo(SfzRegion r)
    {
        if (!r.Has("pitchlfo_freq") && !r.Has("pitchlfo_depth")) return null;
        return new LFOSettings
        {
            DelaySeconds = r.GetDouble("pitchlfo_delay", 0),
            FadeSeconds = r.GetDouble("pitchlfo_fade", 0),
            FrequencyHz = r.GetDouble("pitchlfo_freq", 0),
            PitchDepthCents = r.GetDouble("pitchlfo_depth", 0),
        };
    }

    private static LFOSettings? BuildModLfo(SfzRegion r)
    {
        bool hasAmp = r.Has("amplfo_freq") || r.Has("amplfo_depth");
        bool hasFil = r.Has("fillfo_freq") || r.Has("fillfo_depth");
        if (!hasAmp && !hasFil) return null;

        // One IR mod-LFO carries both tremolo and filter sweep; take whichever
        // frequency/delay is specified (amp first).
        double freq = hasAmp ? r.GetDouble("amplfo_freq", 0) : r.GetDouble("fillfo_freq", 0);
        double delay = hasAmp ? r.GetDouble("amplfo_delay", 0) : r.GetDouble("fillfo_delay", 0);
        double fade = hasAmp ? r.GetDouble("amplfo_fade", 0) : r.GetDouble("fillfo_fade", 0);
        return new LFOSettings
        {
            DelaySeconds = delay,
            FadeSeconds = fade,
            FrequencyHz = freq,
            VolumeDepthDb = r.GetDouble("amplfo_depth", 0),
            FilterDepthCents = r.GetDouble("fillfo_depth", 0),
        };
    }

    /// <summary>
    /// Parses the SFZ v2 generic LFOs (<c>lfoN_*</c>) into <see cref="GenericLfo"/>s. Phase 1 reads the
    /// oscillator (freq/delay/fade/phase + main <c>wave</c>, v2 default sine) and the direct
    /// pitch/volume/cutoff targets; CC modulation, sub-stages and EQ/pan/amp/width targets are added in
    /// later phases (their opcodes stay reported as unsupported until then). Returns null when the
    /// region declares no lfoN_ opcodes — SF2/DLS and plain SFZ zones keep <see cref="PatchZone.Lfos"/>
    /// null and the voice's generic-LFO path dormant.
    /// </summary>
    private static GenericLfo[]? BuildGenericLfos(SfzRegion r)
    {
        // Group lfo{index}_{suffix} opcodes by index (sorted so the result is deterministic).
        SortedDictionary<int, Dictionary<string, string>>? byIndex = null;
        foreach (var kv in r.Opcodes)
        {
            string k = kv.Key;
            if (!k.StartsWith("lfo", StringComparison.Ordinal)) continue;
            int s = 3, p = 3;
            while (p < k.Length && char.IsDigit(k[p])) p++;
            if (p == s || p >= k.Length || k[p] != '_') continue;          // need digits then '_'
            if (!int.TryParse(k.Substring(s, p - s), out int idx)) continue;
            string suffix = k.Substring(p + 1);
            byIndex ??= new SortedDictionary<int, Dictionary<string, string>>();
            if (!byIndex.TryGetValue(idx, out var map))
                byIndex[idx] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            map[suffix] = kv.Value;
        }
        if (byIndex == null) return null;

        var lfos = new List<GenericLfo>();
        foreach (var pair in byIndex)
        {
            var map = pair.Value;

            double Get(string key, double def) =>
                map.TryGetValue(key, out var v) && double.TryParse(v.Trim(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out double d) ? d : def;
            int GetI(string key, int def) =>
                map.TryGetValue(key, out var v) && int.TryParse(v.Trim(), out int d) ? d : def;

            // Collect lfoN_{prefix}{cc} CC modulations (e.g. freq_oncc117, pitch_oncc1).
            LfoCcDepth[]? CcMods(string prefix)
            {
                List<LfoCcDepth>? list = null;
                foreach (var kv in map)
                {
                    if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (!int.TryParse(kv.Key.Substring(prefix.Length), out int cc)) continue;
                    if (!double.TryParse(kv.Value.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double amt)) continue;
                    (list ??= new List<LfoCcDepth>()).Add(new LfoCcDepth(cc, amt));
                }
                return list?.ToArray();
            }

            var targets = new List<LfoTarget>();
            void AddTarget(string key, LfoDestination dest)
            {
                var depthCc = CcMods(key + "_oncc");
                // A target exists if it has a base depth OR a CC-driven depth (mod-wheel vibrato:
                // lfoN_pitch_onccN with no base lfoN_pitch).
                if (map.ContainsKey(key) || depthCc != null)
                    targets.Add(new LfoTarget { Destination = dest, Depth = Get(key, 0), DepthCc = depthCc });
            }
            AddTarget("pitch", LfoDestination.Pitch);
            AddTarget("volume", LfoDestination.Volume);
            AddTarget("cutoff", LfoDestination.Cutoff);

            // EQ targets: lfoN_eq{band}gain / lfoN_eq{band}freq (+ their _onccN depth).
            void AddEqTarget(string key, LfoDestination dest, int band)
            {
                var depthCc = CcMods(key + "_oncc");
                if (map.ContainsKey(key) || depthCc != null)
                    targets.Add(new LfoTarget { Destination = dest, Depth = Get(key, 0), EqBand = band, DepthCc = depthCc });
            }
            for (int b = 1; b <= 8; b++)
            {
                AddEqTarget("eq" + b + "gain", LfoDestination.EqGain, b);
                AddEqTarget("eq" + b + "freq", LfoDestination.EqFreq, b);
            }

            var freqCc = CcMods("freq_oncc");
            bool hasFreq = map.ContainsKey("freq");
            if (!hasFreq && freqCc == null && targets.Count == 0) continue;

            // Stage 0 = the main waveform (v2 default sine); stages 2..8 are additive sub-waveforms
            // (lfoN_waveX/ratioX/scaleX/offsetX), each at a frequency ratio, amplitude scale and DC offset.
            var stages = new List<LfoStage> { new LfoStage(GetI("wave", 1), 1.0, 1.0, 0.0) };
            for (int x = 2; x <= 8; x++)
            {
                string sx = x.ToString();
                if (!map.ContainsKey("wave" + sx) && !map.ContainsKey("ratio" + sx)
                    && !map.ContainsKey("scale" + sx) && !map.ContainsKey("offset" + sx)) continue;
                stages.Add(new LfoStage(GetI("wave" + sx, 1), Get("ratio" + sx, 1.0),
                    Get("scale" + sx, 1.0), Get("offset" + sx, 0.0)));
            }

            lfos.Add(new GenericLfo
            {
                FrequencyHz = Get("freq", 0),
                DelaySeconds = Get("delay", 0),
                FadeSeconds = Get("fade", 0),
                Phase = Get("phase", 0),
                Stages = stages.ToArray(),
                FreqCc = freqCc,
                Targets = targets.ToArray(),
            });
        }
        return lfos.Count > 0 ? lfos.ToArray() : null;
    }

    /// <summary>
    /// Parses SFZ v2 flex envelopes (<c>egN_*</c>): timed level segments (egN_timeX/levelX), a sustain
    /// point (egN_sustain), and pitch/cutoff/volume targets (egN_{target} + egN_{target}_onccX). Returns
    /// null when the region declares none. Other targets (pan/eq/amplitude) and post-sustain release
    /// segments aren't modelled yet (those opcodes stay reported).
    /// </summary>
    private static GenericEg[]? BuildGenericEgs(SfzRegion r)
    {
        SortedDictionary<int, Dictionary<string, string>>? byIndex = null;
        foreach (var kv in r.Opcodes)
        {
            string k = kv.Key;
            if (!k.StartsWith("eg", StringComparison.Ordinal)) continue;
            int s = 2, p = 2;
            while (p < k.Length && char.IsDigit(k[p])) p++;
            if (p == s || p >= k.Length || k[p] != '_') continue;
            if (!int.TryParse(k.Substring(s, p - s), out int idx)) continue;
            string suffix = k.Substring(p + 1);
            byIndex ??= new SortedDictionary<int, Dictionary<string, string>>();
            if (!byIndex.TryGetValue(idx, out var map))
                byIndex[idx] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            map[suffix] = kv.Value;
        }
        if (byIndex == null) return null;

        var egs = new List<GenericEg>();
        foreach (var pair in byIndex)
        {
            var map = pair.Value;
            double Get(string key, double def) =>
                map.TryGetValue(key, out var v) && double.TryParse(v.Trim(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out double d) ? d : def;

            // Segments: time{X}/level{X}, indexed from 0.
            var stageIdx = new SortedSet<int>();
            foreach (var key in map.Keys)
            {
                if (key.StartsWith("time", StringComparison.Ordinal) && int.TryParse(key.AsSpan(4), out int ti)) stageIdx.Add(ti);
                else if (key.StartsWith("level", StringComparison.Ordinal) && int.TryParse(key.AsSpan(5), out int li)) stageIdx.Add(li);
            }
            if (stageIdx.Count == 0) continue;
            int maxStage = 0;
            foreach (var i in stageIdx) maxStage = Math.Max(maxStage, i);
            var stages = new EgStage[maxStage + 1];
            for (int i = 0; i <= maxStage; i++)
                stages[i] = new EgStage(Get("time" + i, 0), Get("level" + i, 0));

            LfoCcDepth[]? CcMods(string prefix)
            {
                List<LfoCcDepth>? list = null;
                foreach (var kv in map)
                {
                    if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (!int.TryParse(kv.Key.Substring(prefix.Length), out int cc)) continue;
                    if (!double.TryParse(kv.Value.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double amt)) continue;
                    (list ??= new List<LfoCcDepth>()).Add(new LfoCcDepth(cc, amt));
                }
                return list?.ToArray();
            }

            var targets = new List<EgTarget>();
            void AddTarget(string key, LfoDestination dest)
            {
                var depthCc = CcMods(key + "_oncc");
                if (map.ContainsKey(key) || depthCc != null)
                    targets.Add(new EgTarget { Destination = dest, Depth = Get(key, 0), DepthCc = depthCc });
            }
            AddTarget("pitch", LfoDestination.Pitch);
            AddTarget("cutoff", LfoDestination.Cutoff);
            AddTarget("volume", LfoDestination.Volume);
            if (targets.Count == 0) continue;

            int sustain = map.TryGetValue("sustain", out var sv) && int.TryParse(sv.Trim(), out int ss) ? ss : -1;
            egs.Add(new GenericEg { Stages = stages, SustainStage = sustain, Targets = targets.ToArray() });
        }
        return egs.Count > 0 ? egs.ToArray() : null;
    }

    private static (LoopMode Mode, long? Start, long? End) ResolveLoop(SfzRegion r, AudioInfo wav)
    {
        string? mode = r.Get("loop_mode") ?? r.Get("loopmode");
        LoopMode loopMode = mode?.ToLowerInvariant() switch
        {
            "no_loop" => LoopMode.None,
            "one_shot" => LoopMode.None,        // plays whole sample; ignores NoteOff
            "loop_continuous" => LoopMode.Continuous,
            "loop_sustain" => LoopMode.UntilRelease,
            null => wav.HasLoop ? LoopMode.Continuous : LoopMode.None,  // SFZ default
            _ => LoopMode.None,
        };

        if (loopMode == LoopMode.None) return (LoopMode.None, null, null);

        long start = r.Has("loop_start") ? r.GetInt("loop_start", 0)
                   : wav.HasLoop ? wav.LoopStartFrame : 0;
        long end = r.Has("loop_end") ? r.GetInt("loop_end", 0)
                 : wav.HasLoop ? wav.LoopEndFrame : wav.FrameCount;
        return (loopMode, start, end);
    }

    /// <summary>
    /// Assembles a zone's modulation routes: explicit <c>_oncc</c> CC modulations
    /// first, then the GM-default controller routes for any (CC, destination) the
    /// bank didn't already wire, then the velocity→amplitude route.
    /// </summary>
    /// <summary>
    /// Effective amp_veltrack (percent): base (default 100) plus each amp_veltrack_oncc{N} shaped by
    /// amp_veltrack_curvecc{N} and evaluated at the seeded initial CC. Falls back to an unseeded CC's
    /// raw depth when the base is zeroed (legacy banks that drive veltrack from a host-set CC).
    /// </summary>
    private static double ComputeEffectiveVeltrack(SfzRegion r, IReadOnlyDictionary<int, int> initialCc,
        IReadOnlyDictionary<int, double[]> curves)
    {
        double veltrack = r.GetDouble("amp_veltrack", 100.0);
        foreach (var (param, cc, value) in r.EnumerateModulations())
        {
            if (param != "amp_veltrack" || !initialCc.TryGetValue(cc, out int ccVal)) continue;
            int curveIdx = r.GetInt("amp_veltrack_curvecc" + cc, 0);
            veltrack += EvalCurve(curveIdx, ccVal / 127.0, curves) * value;
        }
        if (veltrack == 0)
        {
            foreach (var (param, cc, value) in r.EnumerateModulations())
                if (param == "amp_veltrack" && !initialCc.ContainsKey(cc)) return value;
        }
        return veltrack;
    }

    /// <summary>
    /// 128-entry velocity→linear-gain table from amp_veltrack, matching sfizz: with the velocity curve
    /// vel² and veltrack vt∈[-1,1], gain = |vt|·(1 - vel²) then (vt ≥ 0 ? 1 - that : that). veltrack=100
    /// gives the familiar vel² curve; 0 gives flat unity; negative inverts.
    /// </summary>
    private static double[] BuildVeltrackCurve(double veltrackPercent)
    {
        double vt = Math.Clamp(veltrackPercent / 100.0, -1.0, 1.0);
        var table = new double[128];
        for (int v = 0; v < 128; v++)
        {
            double vn = v / 127.0;
            double g = Math.Abs(vt) * (1.0 - vn * vn);
            table[v] = Math.Clamp(vt < 0 ? g : 1.0 - g, 0.0, 1.0);
        }
        return table;
    }

    /// <summary>
    /// Multiplies the SFZ velocity crossfade (xfin_lovel/hivel, xfout_lovel/hivel) into the velocity→
    /// gain table in place. No-op (and untouched) unless the region sets one of those opcodes. The
    /// fade shape follows xf_velcurve (default "power" = equal-power sqrt; "gain" = linear), matching
    /// sfizz including its 1/127 gap offset.
    /// </summary>
    private static void ApplyVelocityCrossfade(SfzRegion r, double[] velGain)
    {
        if (!r.Has("xfin_lovel") && !r.Has("xfin_hivel") && !r.Has("xfout_lovel") && !r.Has("xfout_hivel"))
            return;

        double inLo = r.GetInt("xfin_lovel", 0) / 127.0;
        double inHi = r.GetInt("xfin_hivel", 0) / 127.0;
        double outLo = r.GetInt("xfout_lovel", 127) / 127.0;
        double outHi = r.GetInt("xfout_hivel", 127) / 127.0;
        bool power = !string.Equals(r.Get("xf_velcurve")?.Trim(), "gain", StringComparison.OrdinalIgnoreCase);

        for (int v = 0; v < 128; v++)
        {
            double x = v / 127.0;
            velGain[v] *= CrossfadeIn(inLo, inHi, x, power) * CrossfadeOut(outLo, outHi, x, power);
        }
    }

    /// <summary>
    /// Builds a 128-entry key→gain crossfade table (xfin_lokey/hikey + xfout_lokey/hikey), or null
    /// when the region sets none. The note's key is fixed at NoteOn — like velocity — so this is a
    /// static per-note factor, not a live route. Thresholds parse through GetKey (note names +
    /// octave/note offsets). Shape follows xf_keycurve (default "power" = equal-power sqrt; "gain" =
    /// linear), matching the velocity crossfade including its 1/127 gap offset.
    /// </summary>
    private static double[]? BuildKeyCrossfade(SfzRegion r, SfzControl control)
    {
        if (!r.Has("xfin_lokey") && !r.Has("xfin_hikey") && !r.Has("xfout_lokey") && !r.Has("xfout_hikey"))
            return null;

        double inLo = (r.GetKey("xfin_lokey", control) ?? 0) / 127.0;
        double inHi = (r.GetKey("xfin_hikey", control) ?? 0) / 127.0;
        double outLo = (r.GetKey("xfout_lokey", control) ?? 127) / 127.0;
        double outHi = (r.GetKey("xfout_hikey", control) ?? 127) / 127.0;
        bool power = !string.Equals(r.Get("xf_keycurve")?.Trim(), "gain", StringComparison.OrdinalIgnoreCase);

        var keyGain = new double[128];
        for (int k = 0; k < 128; k++)
        {
            double x = k / 127.0;
            keyGain[k] = CrossfadeIn(inLo, inHi, x, power) * CrossfadeOut(outLo, outHi, x, power);
        }
        return keyGain;
    }

    /// <summary>
    /// Builds the live CC crossfades (xfin/xfout_locc{N}/hicc{N}), or null when the region sets none.
    /// Unlike velocity/key crossfades, the controller value changes during play, so each crossfade is
    /// kept as a 128-entry gain table the voice indexes by the live CC value every block (mod-wheel
    /// layer morphing). Shape follows xf_cccurve (default "power" = equal-power sqrt; "gain" = linear).
    /// </summary>
    private static CcCrossfade[]? BuildCcCrossfades(SfzRegion r)
    {
        SortedSet<int>? ccs = null;
        foreach (var prefix in new[] { "xfin_locc", "xfin_hicc", "xfout_locc", "xfout_hicc" })
            foreach (var (cc, _) in r.EnumerateCc(prefix))
                (ccs ??= new SortedSet<int>()).Add(cc);
        if (ccs == null) return null;

        bool power = !string.Equals(r.Get("xf_cccurve")?.Trim(), "gain", StringComparison.OrdinalIgnoreCase);
        var result = new CcCrossfade[ccs.Count];
        int idx = 0;
        foreach (int cc in ccs)
        {
            double inLo = r.GetInt("xfin_locc" + cc, 0) / 127.0;
            double inHi = r.GetInt("xfin_hicc" + cc, 0) / 127.0;
            double outLo = r.GetInt("xfout_locc" + cc, 127) / 127.0;
            double outHi = r.GetInt("xfout_hicc" + cc, 127) / 127.0;

            var gain = new double[128];
            for (int v = 0; v < 128; v++)
            {
                double x = v / 127.0;
                gain[v] = CrossfadeIn(inLo, inHi, x, power) * CrossfadeOut(outLo, outHi, x, power);
            }
            result[idx++] = new CcCrossfade(cc, gain);
        }
        return result;
    }

    private const double XfadeGap = 1.0 / 127.0;

    private static double CrossfadeIn(double lo, double hi, double x, bool power)
    {
        if (x < lo) return 0.0;
        double length = (hi - lo) - XfadeGap;
        if (length <= 0.0) return 1.0;
        if (x < hi) { double pos = (x - lo) / length; return power ? Math.Sqrt(pos) : pos; }
        return 1.0;
    }

    private static double CrossfadeOut(double lo, double hi, double x, bool power)
    {
        double length = (hi - lo) - XfadeGap;
        if (length <= 0.0) return 1.0;
        if (x > lo)
        {
            double pos = (x - lo) / length;
            if (pos > 1.0) return 0.0;
            return power ? Math.Sqrt(1.0 - pos) : (1.0 - pos);
        }
        return 1.0;
    }

    private static List<ModulationRoute> BuildRoutes(SfzRegion r, IReadOnlyDictionary<int, int> initialCc,
        IReadOnlyDictionary<int, double[]> curves)
    {
        var routes = new List<ModulationRoute>();
        var handled = new HashSet<(int Cc, ModDestination Dest)>();

        foreach (var (param, cc, value) in r.EnumerateModulations())
        {
            var source = MapCcSource(cc);
            if (source == null) continue;

            // A custom <curve> (e.g. tune_curvecc16) shapes the CC response; built-in curves keep the
            // route's continuous transform (null table). pan/cutoff/tune curves are only applied when custom.
            double[]? Curve(string p) => ResolveCurveTable(r.GetInt(p + "_curvecc" + cc, 0), curves);

            ModulationRoute? route = param switch
            {
                // pan_oncc: 0.1%-style ±100 span → normalized pan, linear (or pan_curvecc when custom).
                "pan" => Route(source, ModDestination.PanNormalized, value / 100.0, ModTransform.Linear, Curve("pan")),
                // volume_oncc (gain_cc is sfizz's alias for it): dB, linear (CC up = louder = less attenuation).
                "volume" or "gain" => Route(source, ModDestination.AttenuationDb, -value, ModTransform.Linear),
                // amplitude_oncc: CC → linear gain via the ARIA curve (amplitude_curvecc, default linear),
                // converted to attenuation in the route evaluator. depth/100 is the gain at full curve.
                "amplitude" => new ModulationRoute
                {
                    Source = source,
                    Dest = ModDestination.AttenuationDb,
                    Amount = value / 100.0,
                    Transform = ModTransform.AmplitudeCurve,
                    CurveIndex = r.GetInt("amplitude_curvecc" + cc, 0),
                    CurveTable = Curve("amplitude"),
                },
                "cutoff" => Route(source, ModDestination.FilterCutoffCents, value, ModTransform.Linear, Curve("cutoff")),
                // tune_oncc: CC → pitch (cents), shaped by tune_curvecc (custom curves applied; built-in → linear).
                "tune" => Route(source, ModDestination.PitchCents, value, ModTransform.Linear, Curve("tune")),
                "pitchlfo_depth" => Route(source, ModDestination.VibratoLfoPitchDepthCents, value, ModTransform.Linear),
                _ => null,
            };
            if (route == null) continue;

            routes.Add(route);
            if (cc is >= 0 and <= 127) handled.Add((cc, route.Dest));
        }

        // GM-default controller routes — skip any (CC, destination) a bank _oncc
        // already covers so we don't double-apply (e.g. the bank's pan_oncc10
        // replaces our default CC10 pan route).
        foreach (var d in SfzDefaultRoutes.Routes)
        {
            if (d.Source is ModSource.ChannelController c && handled.Contains((c.Number, d.Dest)))
                continue;
            routes.Add(d);
        }

        // Pitch wheel → pitch, scaled by the region's bend_up (default 200 cents = ±2 semitones). SFZ
        // uses bend_up/bend_down rather than the channel's RPN bend range. Symmetric bends (bend_down =
        // -bend_up, the common case) map linearly; asymmetric ranges use bend_up's magnitude.
        double bendUp = r.GetDouble("bend_up", 200.0);
        routes.Add(Route(new ModSource.PitchBend(), ModDestination.PitchCents, bendUp, ModTransform.Linear));

        // fil_veltrack: velocity raises/lowers the filter cutoff (cents at full velocity). Emit it as a
        // velocity→cutoff route — linear in velocity, matching sfizz (cutoff += veltrack · vel).
        double filVeltrack = r.GetDouble("fil_veltrack", 0);
        if (filVeltrack != 0)
            routes.Add(Route(new ModSource.Velocity(), ModDestination.FilterCutoffCents, filVeltrack, ModTransform.Linear));

        // Amp velocity dynamics are handled by the synthesized amp_velcurve table (see Build), not a route.
        return routes;
    }

    private static ModulationRoute Route(ModSource src, ModDestination dest, double amount, ModTransform t,
        double[]? curveTable = null) =>
        new() { Source = src, Dest = dest, Amount = amount, Transform = t, CurveTable = curveTable };

    /// <summary>
    /// Resolves a *_curvecc index to a runtime curve table — but only for CUSTOM &lt;curve&gt; tables.
    /// Built-in indices (0-6) return null so the route keeps its continuous transform (byte-identical),
    /// and unknown built-ins (≥7) with no custom definition fall back to linear.
    /// </summary>
    private static double[]? ResolveCurveTable(int curveIndex, IReadOnlyDictionary<int, double[]> curves)
        => curves.TryGetValue(curveIndex, out var custom) ? custom : null;

    /// <summary>Evaluates a curve at x∈[0,1] for load-time bakes: a custom &lt;curve&gt; table, else built-in.</summary>
    private static double EvalCurve(int index, double x, IReadOnlyDictionary<int, double[]> curves)
    {
        if (curves.TryGetValue(index, out var t))
        {
            int i = Math.Clamp((int)(x * 127.0 + 0.5), 0, 127);
            return t[i];
        }
        return AriaCurve.Eval(index, x);
    }

    /// <summary>
    /// Maps an SFZ CC index to a modulation source. 0-127 are MIDI CCs; the ARIA
    /// extended indices 128-130 are pitch-bend / channel-aftertouch / poly-
    /// aftertouch. Anything else (note velocity, etc.) has no IR source → null.
    /// </summary>
    private static ModSource? MapCcSource(int cc) => cc switch
    {
        >= 0 and <= 127 => new ModSource.ChannelController((byte)cc),
        128 => new ModSource.PitchBend(),
        129 => new ModSource.ChannelPressure(),
        130 => new ModSource.PolyPressure(),
        _ => null,
    };

    private static int ParseByte(string s, int fallback) =>
        int.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : fallback;
}
