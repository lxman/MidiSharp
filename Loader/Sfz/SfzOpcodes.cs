using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Loader.Sfz;

/// <summary>
/// The authoritative set of SFZ opcodes the translator (<see cref="SfzZoneTranslator"/>)
/// and parser actually act on, plus helpers to classify and describe the rest. This is the
/// single source of truth for the load diagnostic: an opcode not recognized here is one the
/// loader silently drops, so the diagnostic reports it.
/// </summary>
/// <remarks>
/// IMPORTANT: this list must track what the translator reads. When you teach the translator a
/// new opcode, add it here (and remove it from the "unsupported" expectations in the tests).
/// </remarks>
internal static class SfzOpcodes
{
    // Exact opcode names the translator/parser consume (lowercase).
    private static readonly HashSet<string> HandledExact = new(StringComparer.Ordinal)
    {
        // activation
        "key", "lokey", "hikey", "pitch_keycenter", "lovel", "hivel",
        // tuning
        "tune", "pitch", "transpose", "pitch_keytrack", "bend_up", "bend_down",
        // level / pan
        "volume", "global_volume", "master_volume", "group_volume", "pan",
        // amp envelope
        "ampeg_delay", "ampeg_attack", "ampeg_hold", "ampeg_decay", "ampeg_sustain", "ampeg_release",
        // amp envelope velocity modulation (ampeg_vel2*)
        "ampeg_vel2delay", "ampeg_vel2attack", "ampeg_vel2hold", "ampeg_vel2decay",
        "ampeg_vel2release", "ampeg_vel2sustain",
        // filter
        "cutoff", "fil_type", "resonance", "fil_keytrack", "fil_keycenter", "fil_veltrack",
        // filter envelope
        "fileg_depth", "fileg_delay", "fileg_attack", "fileg_hold", "fileg_decay", "fileg_sustain", "fileg_release",
        // pitch envelope
        "pitcheg_depth", "pitcheg_delay", "pitcheg_attack", "pitcheg_hold", "pitcheg_decay", "pitcheg_sustain", "pitcheg_release",
        // LFOs
        "pitchlfo_freq", "pitchlfo_depth", "pitchlfo_delay",
        "amplfo_freq", "amplfo_depth", "amplfo_delay",
        "fillfo_freq", "fillfo_depth", "fillfo_delay",
        // sample addressing + loop
        "sample", "offset", "end", "loop_mode", "loopmode", "loop_start", "loop_end",
        // round-robin (sequence + random) and keyswitch
        "seq_length", "seq_position", "lorand", "hirand",
        // velocity crossfade (folded into the velocity→gain curve)
        "xfin_lovel", "xfin_hivel", "xfout_lovel", "xfout_hivel", "xf_velcurve",
        "sw_last", "sw_down", "sw_lokey", "sw_hikey", "sw_default",
        // grouping, effects, velocity, program routing
        "group", "off_by", "effect1", "effect2", "amp_veltrack", "loprog", "hiprog",
        // trigger / release
        "trigger", "rt_decay",
        // voice-off (note_polyphony / off_mode / off_time)
        "note_polyphony", "off_mode", "off_time",
        // humanization
        "amp_random", "pitch_random", "delay", "delay_random", "offset_random",
        // <control> settings
        "default_path", "octave_offset", "note_offset",
        // display-only labels — no audio effect, correctly dropped (so not a coverage gap)
        "sw_label", "group_label", "region_label", "master_label", "global_label",
    };

    // Open-ended families of the shape "{prefix}{N}" the translator handles.
    private static readonly string[] NumberedPrefixes =
    {
        "locc", "hicc", "on_locc", "on_hicc", "amp_velcurve_",
        "set_hdcc", "set_cc",   // initial controller seeds (set_hdcc before set_cc — longer prefix first)
    };

    // The "{param}_oncc{N}" / "{param}_cc{N}" modulation params actually routed.
    private static readonly HashSet<string> ModParams = new(StringComparer.Ordinal)
    {
        "pan", "volume", "gain", "amplitude", "cutoff", "pitchlfo_depth", "amp_veltrack",
        // CC→amp-envelope (ampeg_{stage}_oncc) — baked into the envelope at load, not routed
        "ampeg_delay", "ampeg_attack", "ampeg_hold", "ampeg_decay", "ampeg_release", "ampeg_sustain",
    };

    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    // Opcode families (digits collapsed to "N") handled where the number sits mid-name, e.g. eq2_freq.
    private static readonly HashSet<string> HandledFamilies = new(StringComparer.Ordinal)
    {
        "eqN_freq", "eqN_bw", "eqN_gain",
        "label_ccN",   // display-only CC label — no audio effect
        // curve tables we consume when evaluating the corresponding _oncc
        "amplitude_curveccN", "amp_veltrack_curveccN",
        "ampeg_delay_curveccN", "ampeg_attack_curveccN", "ampeg_hold_curveccN",
        "ampeg_decay_curveccN", "ampeg_release_curveccN", "ampeg_sustain_curveccN",
        // v1/ARIA bare-cc envelope aliases (ampeg_{stage}cc{N} ≡ ampeg_{stage}_oncc{N}), baked in EnvCcOffset
        "ampeg_delayccN", "ampeg_attackccN", "ampeg_holdccN",
        "ampeg_decayccN", "ampeg_sustainccN", "ampeg_releaseccN",
    };

    /// <summary>True when the loader actually acts on <paramref name="opcode"/> (lowercase).</summary>
    public static bool IsHandled(string opcode)
    {
        if (HandledExact.Contains(opcode)) return true;

        foreach (var p in NumberedPrefixes)
            if (opcode.Length > p.Length && opcode.StartsWith(p, StringComparison.Ordinal)
                && AllDigits(opcode, p.Length))
                return true;

        if (TryGetModParam(opcode, out string param) && ModParams.Contains(param))
            return true;

        if (HandledFamilies.Contains(Family(opcode)))
            return true;

        return false;
    }

    /// <summary>
    /// Collapse a numbered opcode to a family label so per-CC variants aggregate in the report:
    /// <c>width_oncc21</c> and <c>width_oncc7</c> both become <c>width_onccN</c>.
    /// </summary>
    public static string Family(string opcode) => Digits.Replace(opcode, "N");

    /// <summary>A short human note for a common unsupported (ARIA) opcode family, or null.</summary>
    public static string? Describe(string family)
    {
        if (Descriptions.TryGetValue(family, out var note)) return note;
        if (family.Contains("_curvecc")) return "CC response curve table";
        if (family.Contains("_smoothcc")) return "CC smoothing";
        if (family.Contains("_stepcc")) return "CC step quantization";
        if (family.EndsWith("_onccN", StringComparison.Ordinal) ||
            family.EndsWith("_ccN", StringComparison.Ordinal))
            return "CC modulation of an unsupported destination";
        return null;
    }

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["direction"] = "reverse playback (direction=reverse)",
        ["loop_crossfade"] = "crossfaded looping",
        ["loop_count"] = "fixed loop repeat count",
        ["off_mode"] = "voice-off behavior (fast/normal)",
        ["off_curve"] = "voice-off release curve",
        ["polyphony"] = "voice-count limit",
        ["note_polyphony"] = "per-note voice limit",
        ["oscillator"] = "wavetable oscillator (not sample-based)",
        ["width"] = "stereo width",
        ["position"] = "stereo position",
        ["sw_label"] = "keyswitch label (display only)",
        ["sw_vel"] = "keyswitch velocity mode",
        ["region_label"] = "region label (display only)",
        ["global_label"] = "global label (display only)",
        ["group_label"] = "group label (display only)",
        ["set_ccN"] = "initial CC value",
        ["set_hdccN"] = "initial high-resolution CC value",
        ["label_ccN"] = "CC label (display only)",
        ["amp_random"] = "random per-note gain",
        ["pitch_random"] = "random per-note detune",
        ["delay_random"] = "random per-note start delay",
        ["fil2_type"] = "second filter (not modeled)",
        ["cutoff2"] = "second filter cutoff (not modeled)",
        ["xfin_lokey"] = "key crossfade-in (not modeled)",
        ["xfin_hikey"] = "key crossfade-in (not modeled)",
        ["xfout_lokey"] = "key crossfade-out (not modeled)",
        ["xfout_hikey"] = "key crossfade-out (not modeled)",
        ["xfin_lovel"] = "velocity crossfade-in (not modeled)",
        ["xfin_hivel"] = "velocity crossfade-in (not modeled)",
        ["xfout_lovel"] = "velocity crossfade-out (not modeled)",
        ["xfout_hivel"] = "velocity crossfade-out (not modeled)",
    };

    private static bool AllDigits(string s, int start)
    {
        for (int i = start; i < s.Length; i++)
            if (!char.IsDigit(s[i])) return false;
        return start < s.Length;
    }

    // Find a "{param}_oncc{N}" or "{param}_cc{N}" shape and return the param prefix.
    private static bool TryGetModParam(string key, out string param)
    {
        param = string.Empty;
        int marker = key.IndexOf("_oncc", StringComparison.Ordinal);
        int len = 5;
        if (marker < 0) { marker = key.IndexOf("_cc", StringComparison.Ordinal); len = 3; }
        if (marker <= 0) return false;
        if (!AllDigits(key, marker + len)) return false;
        param = key.Substring(0, marker);
        return true;
    }
}
