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
        // tune (a.k.a. pitch) is cents; transpose is semitones.
        double tuneCents = r.GetDouble("tune", r.GetDouble("pitch", 0));
        double transpose = r.GetDouble("transpose", 0);

        // ── Level / pan ──────────────────────────────────────────────
        // SFZ volume is signed dB, positive = louder; the *_volume variants at
        // each hierarchy level sum with it. IR AttenuationDb is the negation.
        double totalVolumeDb = r.GetDouble("volume", 0)
                             + r.GetDouble("global_volume", 0)
                             + r.GetDouble("master_volume", 0)
                             + r.GetDouble("group_volume", 0);
        double pan = Math.Clamp(r.GetDouble("pan", 0) / 100.0, -1.0, 1.0);

        // ── Amp envelope (DAHDSR; sustain is percent) ────────────────
        var ampEnv = new EnvelopeSettings
        {
            DelaySeconds = r.GetDouble("ampeg_delay", 0),
            AttackSeconds = r.GetDouble("ampeg_attack", 0),
            HoldSeconds = r.GetDouble("ampeg_hold", 0),
            DecaySeconds = r.GetDouble("ampeg_decay", 0),
            SustainLevel = Math.Clamp(r.GetDouble("ampeg_sustain", 100.0) / 100.0, 0, 1),
            // Floor the release so a NoteOff mid-sample fades instead of clicking.
            ReleaseSeconds = Math.Max(r.GetDouble("ampeg_release", 0), 0.003),
        };

        // ── Filter ───────────────────────────────────────────────────
        FilterSettings? filter = BuildFilter(r, out double fileDepthCents);

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
        double[]? ampVelCurve = BuildVelocityCurve(r)
                                ?? BuildVeltrackCurve(ComputeEffectiveVeltrack(r, control.InitialControllers));

        // ── Routes: _oncc modulations + suppressed default-CC routes ──
        var routes = BuildRoutes(r, control.InitialControllers);

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

        // ── Exclusive group (group / off_by) ─────────────────────────
        int? exclusive = null;
        if (r.Has("group")) exclusive = r.GetInt("group", 0);
        else if (r.Has("off_by")) exclusive = r.GetInt("off_by", 0);
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
        double delaySeconds = r.GetDouble("delay", 0);
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

            ReverbSend = Math.Clamp(r.GetDouble("effect1", 0) / 100.0, 0, 1),
            ChorusSend = Math.Clamp(r.GetDouble("effect2", 0) / 100.0, 0, 1),

            Routes = routes,
            AmpVelCurve = ampVelCurve,
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
    /// Evaluates an ARIA built-in CC curve at normalised input x∈[0,1] (matches sfizz's predefined
    /// curves 0–6). Curves are linear ramps (0–3) and quadratic/sqrt shapes (4–6); unknown → linear.
    /// Custom &lt;curve&gt; definitions aren't modelled yet — they fall back to linear here.
    /// </summary>
    private static double EvalBuiltinCurve(int index, double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        return index switch
        {
            0 => x,                  // 0 → 1
            1 => 2 * x - 1,          // -1 → +1
            2 => 1 - x,              // 1 → 0
            3 => 1 - 2 * x,          // +1 → -1
            4 => x * x,              // concave (slow rise)
            5 => Math.Sqrt(x),       // convex (fast rise)
            6 => Math.Sqrt(1 - x),   // 1 → 0 convex
            _ => x,
        };
    }

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

    private static FilterSettings? BuildFilter(SfzRegion r, out double envDepthCents)
    {
        envDepthCents = r.GetDouble("fileg_depth", 0);
        bool hasCutoff = r.Has("cutoff");
        if (!hasCutoff && envDepthCents == 0) return null;

        double cutoff = r.GetDouble("cutoff", 20000.0);
        var type = (r.Get("fil_type") ?? "lpf_2p").ToLowerInvariant() switch
        {
            var s when s.StartsWith("hpf") => FilterType.HighPass,
            var s when s.StartsWith("bpf") => FilterType.BandPass,
            var s when s.StartsWith("brf") => FilterType.Notch,
            var s when s.StartsWith("lsh") => FilterType.LowShelf,
            var s when s.StartsWith("hsh") => FilterType.HighShelf,
            _ => FilterType.LowPass,
        };
        return new FilterSettings
        {
            Type = type,
            CutoffHz = cutoff,
            ResonanceDb = r.GetDouble("resonance", 0),
            KeyTrackCentsPerKey = r.GetDouble("fil_keytrack", 0),
            EnvelopeDepthCents = envDepthCents,
            LfoDepthCents = r.GetDouble("fillfo_depth", 0),
        };
    }

    private static LFOSettings? BuildPitchLfo(SfzRegion r)
    {
        if (!r.Has("pitchlfo_freq") && !r.Has("pitchlfo_depth")) return null;
        return new LFOSettings
        {
            DelaySeconds = r.GetDouble("pitchlfo_delay", 0),
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
        return new LFOSettings
        {
            DelaySeconds = delay,
            FrequencyHz = freq,
            VolumeDepthDb = r.GetDouble("amplfo_depth", 0),
            FilterDepthCents = r.GetDouble("fillfo_depth", 0),
        };
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
    private static double ComputeEffectiveVeltrack(SfzRegion r, IReadOnlyDictionary<int, int> initialCc)
    {
        double veltrack = r.GetDouble("amp_veltrack", 100.0);
        foreach (var (param, cc, value) in r.EnumerateModulations())
        {
            if (param != "amp_veltrack" || !initialCc.TryGetValue(cc, out int ccVal)) continue;
            int curveIdx = r.GetInt("amp_veltrack_curvecc" + cc, 0);
            veltrack += EvalBuiltinCurve(curveIdx, ccVal / 127.0) * value;
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

    private static List<ModulationRoute> BuildRoutes(SfzRegion r, IReadOnlyDictionary<int, int> initialCc)
    {
        var routes = new List<ModulationRoute>();
        var handled = new HashSet<(int Cc, ModDestination Dest)>();

        foreach (var (param, cc, value) in r.EnumerateModulations())
        {
            var source = MapCcSource(cc);
            if (source == null) continue;

            ModulationRoute? route = param switch
            {
                // pan_oncc: 0.1%-style ±100 span → normalized pan, linear.
                "pan" => Route(source, ModDestination.PanNormalized, value / 100.0, ModTransform.Linear),
                // volume_oncc (gain_cc is sfizz's alias for it): dB, linear (CC up = louder = less attenuation).
                "volume" or "gain" => Route(source, ModDestination.AttenuationDb, -value, ModTransform.Linear),
                // amplitude_oncc: percentage gain → concave attenuation, like a volume CC.
                "amplitude" => Route(source, ModDestination.AttenuationDb, 96.0 * value / 100.0, ModTransform.ConcaveUnipolarNegative),
                "cutoff" => Route(source, ModDestination.FilterCutoffCents, value, ModTransform.Linear),
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

        // Velocity dynamics are handled by the synthesized amp_velcurve table (see Build), not a route.
        return routes;
    }

    private static ModulationRoute Route(ModSource src, ModDestination dest, double amount, ModTransform t) =>
        new() { Source = src, Dest = dest, Amount = amount, Transform = t };

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
