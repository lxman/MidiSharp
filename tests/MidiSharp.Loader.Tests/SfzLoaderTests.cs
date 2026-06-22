using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MidiSharp.Loader.Sfz;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Loader.Tests;

/// <summary>
/// End-to-end SFZ loader tests driven through the public
/// <see cref="SoundBankLoader"/> surface. Each test writes a synthetic .sfz and
/// the WAV(s) it references into a temp directory, loads it, and asserts on the
/// resulting public IR — so the parser, opcode translator, WAV reader, and
/// sample source are all exercised together.
/// </summary>
public sealed class SfzLoaderTests : IDisposable
{
    private readonly string _dir;

    public SfzLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sfztest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private string WriteSfz(string body)
    {
        string path = Path.Combine(_dir, "instrument.sfz");
        File.WriteAllText(path, body, Encoding.UTF8);
        return path;
    }

    private string WriteNamed(string name, string body)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, body, Encoding.UTF8);
        return path;
    }

    /// <summary>Writes a minimal 16-bit mono PCM WAV (a quiet ramp); optional smpl loop.</summary>
    private void WriteWav(string relPath, int frames = 256, int sampleRate = 44100, (int start, int end)? loop = null)
    {
        string full = Path.Combine(_dir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using FileStream fs = File.Create(full);
        using var w = new BinaryWriter(fs);

        int dataBytes = frames * 2;
        bool hasLoop = loop.HasValue;
        int smplBytes = hasLoop ? 36 + 24 : 0;

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(4 + 8 + 16 + (hasLoop ? 8 + smplBytes : 0) + 8 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);            // PCM
        w.Write((short)1);            // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);      // byte rate
        w.Write((short)2);            // block align
        w.Write((short)16);           // bits

        if (hasLoop)
        {
            w.Write(Encoding.ASCII.GetBytes("smpl"));
            w.Write(smplBytes);
            for (var i = 0; i < 7; i++) w.Write(0);  // manuf..smpteOffset
            w.Write(1);                               // numLoops
            w.Write(0);                               // samplerData
            w.Write(0);                               // cuePointId
            w.Write(0);                               // type = forward
            w.Write(loop!.Value.start);
            w.Write(loop.Value.end);
            w.Write(0);                               // fraction
            w.Write(0);                               // playCount (infinite)
        }

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        for (var i = 0; i < frames; i++)
            w.Write((short)(i % 64 * 32));  // tiny ramp, well below clipping
    }

    private static PatchZone[] ZonesOf(IRBank bank)
    {
        Patch? patch = bank.FindPatch(0, 0);
        Assert.NotNull(patch);
        return patch!.Zones.ToArray();
    }

    // ── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public void LazySample_decodes_in_background_then_reads_correct_data()
    {
        WriteWav("samples/a.wav", frames: 256);
        string path = WriteSfz("""
                               <control> default_path=samples/
                               <region> sample=a.wav lokey=0 hikey=127
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        Assert.True(bank.Samples.Count >= 1);

        // Lazy: the first read kicks a background decode and returns silence (0); poll until it lands.
        var buf = new float[256];
        var n = 0;
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (n == 0 && DateTime.UtcNow < deadline)
        {
            n = bank.Samples.ReadFrames(0, 0, buf);
            if (n == 0) Thread.Sleep(5);
        }

        Assert.True(n > 0, "background decode never produced data");
        // WriteWav lays down a known ramp: sample i = (i % 64 * 32) as int16, normalised by 1/32768.
        for (var i = 0; i < n; i++)
            Assert.Equal((short)(i % 64 * 32) / 32768.0, buf[i], 5);
    }

    [Fact]
    public void BlockingSampleDecode_returns_data_on_the_first_read()
    {
        WriteWav("samples/a.wav", frames: 256);
        string path = WriteSfz("""
                               <control> default_path=samples/
                               <region> sample=a.wav lokey=0 hikey=127
                               """);

        // Offline-render mode: the first ReadFrames must decode synchronously and return the sample
        // data — no background race, no transient silence. (The lazy default returns 0 on the first
        // call; see LazySample_decodes_in_background_then_reads_correct_data.) This guards the fix for
        // fast first-hit notes losing their attack in a WAV export.
        using IRBank bank = SoundBankLoader.Load(path, new SoundBankLoadOptions { BlockingSampleDecode = true });

        var buf = new float[256];
        int n = bank.Samples.ReadFrames(0, 0, buf);   // single call, no polling

        Assert.True(n > 0, "blocking decode should return data on the first read");
        for (var i = 0; i < n; i++)
            Assert.Equal((short)(i % 64 * 32) / 32768.0, buf[i], 5);
    }

    [Fact]
    public void Amp_veltrack_oncc_curve_reduces_velocity_tracking()
    {
        WriteWav("a.wav");
        string path = WriteSfz("""
                               <control> set_hdcc99=0.73
                               <region> sample=a.wav key=60 amp_veltrack_oncc99=-100 amp_veltrack_curvecc99=2
                               """);
        PatchZone zone = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        // effective veltrack = 100 + curve2(0.73)*(-100) = 73.2. Synthesized velocity curve:
        // vel 0 → 1 - 0.732 = 0.268, vel 127 → 1.0. (Full 100% veltrack would be 0 at vel 0 — the
        // CC curve pulls low-velocity gain up, i.e. less aggressive tracking.)
        Assert.NotNull(zone.AmpVelCurve);
        Assert.InRange(zone.AmpVelCurve![0], 0.24, 0.30);
        Assert.Equal(1.0, zone.AmpVelCurve[127], 3);
    }

    [Fact]
    public void Set_cc_and_set_hdcc_seed_initial_controllers()
    {
        WriteWav("a.wav");
        string path = WriteSfz("""
                               <control> set_cc7=100 set_hdcc99=0.73
                               <region> sample=a.wav key=60
                               """);
        using IRBank bank = SoundBankLoader.Load(path);
        Assert.Equal(100, bank.InitialControllers[7]);
        Assert.Equal(93, bank.InitialControllers[99]);   // 0.73 × 127 = 92.71 → 93
    }

    [Fact]
    public void Ampeg_release_oncc_bakes_into_release_at_seeded_cc()
    {
        WriteWav("a.wav");
        // cc72 seeded to 0.5 (≈64); ampeg_release_oncc72=2 with the default linear curve → +2×0.5 ≈ 1 s.
        string path = WriteSfz("""
                               <control> set_hdcc72=0.5
                               <region> sample=a.wav key=60 ampeg_release_oncc72=2
                               """);
        EnvelopeSettings ve = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0].VolumeEnvelope;
        Assert.InRange(ve.ReleaseSeconds, 0.95, 1.05);
    }

    [Fact]
    public void Ampeg_attack_bare_cc_alias_bakes_like_oncc()
    {
        WriteWav("a.wav");
        // v1/ARIA short form: ampeg_attackcc72 (no underscore) is an alias of ampeg_attack_oncc72.
        // cc72 seeded to 0.5 (≈64), linear curve → +2×0.5 ≈ 1 s on top of the 0.1 s base attack.
        string path = WriteSfz("""
                               <control> set_hdcc72=0.5
                               <region> sample=a.wav key=60 ampeg_attack=0.1 ampeg_attackcc72=2
                               """);
        EnvelopeSettings ve = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0].VolumeEnvelope;
        Assert.InRange(ve.AttackSeconds, 1.05, 1.15);
    }

    [Fact]
    public void Delay_cc_offsets_region_start_delay_at_seeded_cc()
    {
        WriteWav("a.wav");
        // cc20 seeded to 64 (≈0.5); delay_cc20=2 with the default linear curve → +2×0.5 ≈ 1 s,
        // on top of the 0.1 s base delay.
        string path = WriteSfz("""
                               <control> set_cc20=64
                               <region> sample=a.wav key=60 delay=0.1 delay_cc20=2
                               """);
        PatchZone z = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        Assert.InRange(z.DelaySeconds, 1.05, 1.15);
    }

    [Fact]
    public void Generic_v2_lfo_parses_oscillator_and_direct_targets()
    {
        WriteWav("a.wav");
        PatchZone z = SoundBankLoader.Load(WriteSfz(
            "<region> sample=a.wav key=60 lfo01_freq=5 lfo01_wave=1 lfo01_delay=0.2 lfo01_fade=0.5 " +
            "lfo01_pitch=50 lfo01_volume=3")).FindPatch(0, 0)!.Zones[0];
        Assert.NotNull(z.Lfos);
        GenericLfo lfo = Assert.Single(z.Lfos!);
        Assert.Equal(5.0, lfo.FrequencyHz, 3);
        Assert.Equal(0.2, lfo.DelaySeconds, 3);
        Assert.Equal(0.5, lfo.FadeSeconds, 3);
        Assert.Equal(1, lfo.Stages[0].Wave);   // sine
        Assert.Equal(2, lfo.Targets.Length);
        Assert.Contains(lfo.Targets, t => t.Destination == LfoDestination.Pitch && t.Depth == 50);
        Assert.Contains(lfo.Targets, t => t.Destination == LfoDestination.Volume && t.Depth == 3);
    }

    [Fact]
    public void Generic_v2_lfo_parses_cc_modulation_of_freq_and_depth()
    {
        WriteWav("a.wav");
        // mod-wheel vibrato: no base pitch depth, CC1 adds up to 50 cents; CC117 adds up to 8 Hz.
        PatchZone z = SoundBankLoader.Load(WriteSfz(
            "<region> sample=a.wav key=60 lfo01_freq=2 lfo01_freq_oncc117=8 lfo01_pitch_oncc1=50"))
            .FindPatch(0, 0)!.Zones[0];
        GenericLfo lfo = Assert.Single(z.Lfos!);
        Assert.Equal(117, Assert.Single(lfo.FreqCc!).Cc);
        Assert.Equal(8.0, lfo.FreqCc![0].Amount, 3);
        LfoTarget pitch = Assert.Single(lfo.Targets);   // created from the _oncc alone (no base lfo01_pitch)
        Assert.Equal(LfoDestination.Pitch, pitch.Destination);
        Assert.Equal(0.0, pitch.Depth, 3);
        Assert.Equal(50.0, Assert.Single(pitch.DepthCc!).Amount, 3);
    }

    [Fact]
    public void Generic_v2_lfo_parses_complex_multi_stage()
    {
        WriteWav("a.wav");
        // main S&H (12) plus a 4x-faster sine sub-stage at 30% amplitude (the spec's example).
        PatchZone z = SoundBankLoader.Load(WriteSfz(
            "<region> sample=a.wav key=60 lfo01_freq=2 lfo01_pitch=10 lfo01_wave=12 " +
            "lfo01_wave2=1 lfo01_ratio2=4 lfo01_scale2=0.3 lfo01_offset2=0.1"))
            .FindPatch(0, 0)!.Zones[0];
        GenericLfo lfo = Assert.Single(z.Lfos!);
        Assert.Equal(2, lfo.Stages.Length);
        Assert.Equal(12, lfo.Stages[0].Wave);     // main
        Assert.Equal(1, lfo.Stages[1].Wave);      // sub sine
        Assert.Equal(4.0, lfo.Stages[1].Ratio, 3);
        Assert.Equal(0.3, lfo.Stages[1].Scale, 3);
        Assert.Equal(0.1, lfo.Stages[1].Offset, 3);
    }

    [Fact]
    public void Generic_v2_lfo_eq_target_instantiates_band_even_at_zero_gain()
    {
        WriteWav("a.wav");
        // eq2 defined (freq) with no static gain, driven by an LFO whose depth is the mod wheel.
        PatchZone z = SoundBankLoader.Load(WriteSfz(
            "<region> sample=a.wav key=60 eq2_freq=1500 lfo01_freq=3 lfo01_eq2gain_oncc1=6"))
            .FindPatch(0, 0)!.Zones[0];
        // The 0-gain band exists because the LFO drives it, and carries its band number.
        EqBand band = Assert.Single(z.EqBands);
        Assert.Equal(2, band.BandNumber);
        Assert.Equal(1500.0, band.FrequencyHz, 3);
        Assert.Equal(0.0, band.GainDb, 3);
        GenericLfo lfo = Assert.Single(z.Lfos!);
        LfoTarget eqT = Assert.Single(lfo.Targets);
        Assert.Equal(LfoDestination.EqGain, eqT.Destination);
        Assert.Equal(2, eqT.EqBand);
        Assert.Equal(6.0, Assert.Single(eqT.DepthCc!).Amount, 3);
    }

    [Fact]
    public void Lfo_fade_in_time_parses_for_pitch_and_amp()
    {
        WriteWav("a.wav");
        string path = WriteSfz("<region> sample=a.wav key=60 " +
                               "pitchlfo_freq=5 pitchlfo_depth=50 pitchlfo_fade=0.8 " +
                               "amplfo_freq=4 amplfo_depth=3 amplfo_fade=1.2");
        PatchZone zone = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        Assert.Equal(0.8, zone.VibratoLFO!.FadeSeconds, 3);
        Assert.Equal(1.2, zone.ModulationLFO!.FadeSeconds, 3);
    }

    [Fact]
    public void Ampeg_dynamic_emits_unbaked_cc_mods()
    {
        WriteWav("a.wav");
        // With ampeg_dynamic the attack CC is NOT baked into AttackSeconds (the voice evaluates it live);
        // it's emitted as a CcMod. Without ampeg_dynamic the same opcode bakes (covered elsewhere).
        string path = WriteSfz("""
                               <control> set_cc1=64
                               <region> sample=a.wav key=60 ampeg_attack=0.1 ampeg_attackcc1=2 ampeg_dynamic=1
                               """);
        EnvelopeSettings ve = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0].VolumeEnvelope;
        Assert.True(ve.Dynamic);
        Assert.Equal(0.1, ve.AttackSeconds, 3);   // base only — not baked (would be ~1.1 if baked at cc1=64)
        EnvCcMod mod = Assert.Single(ve.CcMods!);
        Assert.Equal(EnvStage.Attack, mod.Stage);
        Assert.Equal(1, mod.Cc);
        Assert.Equal(2.0, mod.Amount, 3);
    }

    [Fact]
    public void Ampeg_vel2_envelope_modulation_parses()
    {
        WriteWav("a.wav");
        string path = WriteSfz("<region> sample=a.wav key=60 ampeg_attack=0.5 ampeg_vel2attack=-0.4 ampeg_vel2decay=0.2 ampeg_vel2sustain=-50");
        EnvelopeSettings ve = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0].VolumeEnvelope;
        Assert.Equal(-0.4, ve.VelToAttackSeconds, 3);
        Assert.Equal(0.2, ve.VelToDecaySeconds, 3);
        Assert.Equal(-0.5, ve.VelToSustainLevel, 3);   // -50% → -0.5 level offset
    }

    [Fact]
    public void Velocity_crossfade_shapes_the_gain_curve()
    {
        WriteWav("a.wav");
        // amp_veltrack=0 → flat velocity gain, so AmpVelCurve reflects only the crossfade. A mid layer:
        // fades in 0→64, out 64→127, so it's silent at the extremes and ~unity at the crossover.
        string path = WriteSfz("<region> sample=a.wav key=60 amp_veltrack=0 xfin_lovel=0 xfin_hivel=64 xfout_lovel=64 xfout_hivel=127");
        double[]? c = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0].AmpVelCurve;
        Assert.NotNull(c);
        Assert.Equal(0.0, c![0], 2);     // faded out at the bottom
        Assert.True(c[64] > 0.9);        // ~peak at the crossover
        Assert.Equal(0.0, c[127], 2);    // faded out at the top
    }

    [Fact]
    public void Key_crossfade_builds_a_per_note_gain_table()
    {
        WriteWav("a.wav");
        // A mid keyboard layer: fades in 36→60, out 60→96 — silent at the extremes, ~unity at C4 (60).
        string path = WriteSfz("<region> sample=a.wav lokey=0 hikey=127 pitch_keycenter=60 " +
                               "xfin_lokey=36 xfin_hikey=60 xfout_lokey=60 xfout_hikey=96");
        double[]? c = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0].AmpKeyCurve;
        Assert.NotNull(c);
        Assert.Equal(0.0, c![24], 2);   // below the fade-in range → silent
        Assert.True(c[60] > 0.9);       // ~peak at the crossover
        Assert.Equal(0.0, c[110], 2);   // above the fade-out range → silent
    }

    [Fact]
    public void Amplitude_percent_folds_into_attenuation()
    {
        WriteWav("a.wav");
        // amplitude=50 → 0.5 linear gain → ~6.02 dB attenuation (volume defaults to 0).
        LevelSettings lvl = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 amplitude=50"))
            .FindPatch(0, 0)!.Zones[0].Level;
        Assert.InRange(lvl.AttenuationDb, 5.9, 6.1);
    }

    [Fact]
    public void Width_sets_normalized_stereo_factor()
    {
        WriteWav("a.wav");
        double def = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60"))
            .FindPatch(0, 0)!.Zones[0].WidthNormalized;
        Assert.Equal(1.0, def, 3);   // default = full stereo
        double w0 = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 width=0"))
            .FindPatch(0, 0)!.Zones[0].WidthNormalized;
        Assert.Equal(0.0, w0, 3);    // mono
        double wNeg = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 width=-100"))
            .FindPatch(0, 0)!.Zones[0].WidthNormalized;
        Assert.Equal(-1.0, wNeg, 3); // swapped
    }

    [Fact]
    public void Cc_crossfade_builds_a_live_gain_table_per_controller()
    {
        WriteWav("a.wav");
        // Mod wheel (CC1) fades this layer in over 0→64; default xf_cccurve = power.
        string path = WriteSfz("<region> sample=a.wav key=60 xfin_locc1=0 xfin_hicc1=64");
        PatchZone zone = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        Assert.NotNull(zone.CcCrossfades);
        CcCrossfade cf = Assert.Single(zone.CcCrossfades!);
        Assert.Equal(1, cf.Cc);
        Assert.Equal(0.0, cf.Gain[0], 2);    // CC1=0 → silent
        Assert.True(cf.Gain[64] > 0.9);      // CC1=64 → full
        Assert.True(cf.Gain[127] > 0.9);     // above hi → stays full
    }

    [Fact]
    public void Custom_curve_is_parsed_and_applied_to_a_route()
    {
        WriteWav("a.wav");
        PatchZone z = SoundBankLoader.Load(WriteSfz("""
                                                    <curve> curve_index=16 v000=0 v064=1 v127=0
                                                    <region> sample=a.wav key=60 tune_oncc34=1200 tune_curvecc34=16
                                                    """)).FindPatch(0, 0)!.Zones[0];
        // The tune route carries the resolved custom curve (the bend route has none).
        ModulationRoute route = z.Routes.Single(rt => rt.Dest == ModDestination.PitchCents && rt.CurveTable != null);
        Assert.Equal(0.0, route.CurveTable![0], 3);    // v000
        Assert.True(route.CurveTable[64] > 0.9);       // v064 peak
        Assert.Equal(0.0, route.CurveTable[127], 3);   // v127
    }

    [Fact]
    public void Cithara_cluster_opcodes_parse()
    {
        WriteWav("a.wav");
        PatchZone z = SoundBankLoader.Load(WriteSfz(
            "<region> sample=a.wav key=60 note_selfmask=off bend_smooth=40 width_oncc31=100 tune_oncc34=-2400"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.False(z.NoteSelfMask);
        Assert.Equal(0.040, z.BendSmoothSeconds, 4);   // ms → seconds
        Assert.Equal(31, Assert.Single(z.WidthCc!).Cc);
        Assert.Equal(100.0, z.WidthCc![0].Amount, 1);
        // tune_oncc34 is a CC → PitchCents route with the given cents amount.
        Assert.Contains(z.Routes, rt => rt.Dest == ModDestination.PitchCents
                                        && Math.Abs(rt.Amount - (-2400)) < 1);
    }

    [Fact]
    public void Flex_eg_parses_stages_sustain_and_cc_target()
    {
        WriteWav("a.wav");
        // The Discord Sitar's eg06: 2 segments, sustain at stage 1, pitch depth driven by CC140.
        PatchZone z = SoundBankLoader.Load(WriteSfz(
            "<region> sample=a.wav key=60 eg06_time0=0.02 eg06_level0=-1 eg06_time1=0.07 eg06_level1=0 " +
            "eg06_sustain=1 eg06_pitch_oncc140=100")).FindPatch(0, 0)!.Zones[0];
        GenericEg eg = Assert.Single(z.Egs!);
        Assert.Equal(2, eg.Stages.Length);
        Assert.Equal(0.02, eg.Stages[0].TimeSeconds, 3);
        Assert.Equal(-1.0, eg.Stages[0].Level, 3);
        Assert.Equal(1, eg.SustainStage);
        EgTarget t = Assert.Single(eg.Targets);
        Assert.Equal(LfoDestination.Pitch, t.Destination);
        Assert.Equal(140, Assert.Single(t.DepthCc!).Cc);
    }

    [Fact]
    public void Pan_keytrack_fil_random_and_pitchlfo_freq_cc_parse()
    {
        WriteWav("a.wav");
        PatchZone z = SoundBankLoader.Load(WriteSfz(
            "<region> sample=a.wav key=60 pan_keytrack=1 pan_keycenter=60 fil_random=150 pitchlfo_freq_oncc76=10"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.Equal(1.0, z.PanKeyTrackPercentPerKey, 3);
        Assert.Equal(60, z.PanKeyTrackCenter);
        Assert.Equal(150.0, z.FilterRandomCents, 3);
        Assert.Equal(76, Assert.Single(z.VibLfoFreqCc!).Cc);
        Assert.Equal(10.0, z.VibLfoFreqCc![0].Amount, 3);
    }

    [Fact]
    public void Amp_keytrack_and_eq_vel2gain_parse()
    {
        WriteWav("a.wav");
        PatchZone z = SoundBankLoader.Load(WriteSfz(
            "<region> sample=a.wav key=60 amp_keytrack=-0.15 amp_keycenter=c2 " +
            "eq1_freq=1000 eq1_vel2gain=6")).FindPatch(0, 0)!.Zones[0];
        Assert.Equal(-0.15, z.AmpKeyTrackDbPerKey, 3);
        Assert.Equal(36, z.AmpKeyTrackCenter);   // c2 = MIDI 36
        EqBand band = Assert.Single(z.EqBands);     // exists despite 0 base gain (velocity drives it)
        Assert.Equal(0.0, band.GainDb, 3);
        Assert.Equal(6.0, band.VelToGainDb, 3);
    }

    [Fact]
    public void Offby_alias_and_group_tune_parse()
    {
        WriteWav("a.wav");
        // offby = no-underscore off_by (exclusive group); group_tune sums onto the region tune.
        PatchZone z = SoundBankLoader.Load(WriteSfz(
            "<region> sample=a.wav key=60 offby=4 tune=10 group_tune=5"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.Equal(4, z.ExclusiveGroup);
        Assert.Equal(15.0, z.Sample!.FineTuneCents, 3);   // 10 + 5
    }

    [Fact]
    public void Second_filter_parses_type_cutoff_and_cc()
    {
        WriteWav("a.wav");
        PatchZone z = SoundBankLoader.Load(WriteSfz(
            "<region> sample=a.wav key=60 fil_type=lpf_2p cutoff=2000 fil2_type=hpf_1p cutoff2=300 cutoff2_cc1=1200"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.Equal(FilterType.LowPass, z.Filter!.Type);
        Assert.NotNull(z.Filter2);
        Assert.Equal(FilterType.HighPass, z.Filter2!.Type);
        Assert.Equal(300.0, z.Filter2.CutoffHz, 1);
        Assert.Equal(1, Assert.Single(z.Filter2CutoffCc!).Cc);
        Assert.Equal(1200.0, z.Filter2CutoffCc![0].Amount, 1);
    }

    [Fact]
    public void Eq_bands_parse_and_skip_zero_gain()
    {
        WriteWav("a.wav");
        string path = WriteSfz("""
                               <region> sample=a.wav key=60
                                   eq1_freq=120 eq1_bw=2 eq1_gain=6
                                   eq2_freq=3000 eq2_gain=0
                                   eq3_freq=8000 eq3_bw=0.5 eq3_gain=-4
                               """);
        PatchZone z = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        // eq2 has 0 dB gain → inactive → skipped; eq1 + eq3 kept in order.
        Assert.Equal(2, z.EqBands.Count);
        Assert.Equal(120.0, z.EqBands[0].FrequencyHz, 3);
        Assert.Equal(2.0, z.EqBands[0].BandwidthOctaves, 3);
        Assert.Equal(6.0, z.EqBands[0].GainDb, 3);
        Assert.Equal(8000.0, z.EqBands[1].FrequencyHz, 3);
        Assert.Equal(-4.0, z.EqBands[1].GainDb, 3);
    }

    [Fact]
    public void Filter_keytrack_keycenter_and_veltrack_are_applied()
    {
        WriteWav("a.wav");
        string path = WriteSfz("<region> sample=a.wav key=60 cutoff=1000 fil_keytrack=100 fil_keycenter=48 fil_veltrack=2400");
        PatchZone z = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        Assert.NotNull(z.Filter);
        Assert.Equal(100.0, z.Filter!.KeyTrackCentsPerKey, 3);
        Assert.Equal(48, z.Filter.KeyTrackCenter);
        // fil_veltrack → a linear velocity→cutoff route.
        ModulationRoute velCut = z.Routes.Single(r => r.Source is ModSource.Velocity && r.Dest == ModDestination.FilterCutoffCents);
        Assert.Equal(2400.0, velCut.Amount, 3);
        Assert.Equal(ModTransform.Linear, velCut.Transform);
    }

    [Fact]
    public void Bend_up_sets_the_pitch_bend_route_range()
    {
        WriteWav("a.wav");
        PatchZone z = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 bend_up=400"))
            .FindPatch(0, 0)!.Zones[0];
        ModulationRoute pb = z.Routes.Single(r => r.Source is ModSource.PitchBend && r.Dest == ModDestination.PitchCents);
        Assert.Equal(400.0, pb.Amount, 3);          // ±4 semitones
        Assert.Null(pb.AmountModulator);             // not RPN-scaled — SFZ uses bend_up directly

        WriteWav("b.wav");
        PatchZone def = SoundBankLoader.Load(WriteNamed("def.sfz", "<region> sample=b.wav key=60"))
            .FindPatch(0, 0)!.Zones[0];
        ModulationRoute pbd = def.Routes.Single(r => r.Source is ModSource.PitchBend);
        Assert.Equal(200.0, pbd.Amount, 3);          // default = ±2 semitones
    }

    [Fact]
    public void Amplitude_oncc_uses_the_aria_curve()
    {
        WriteWav("a.wav");
        string path = WriteSfz("<region> sample=a.wav key=60 amplitude_oncc7=100 amplitude_curvecc7=4");
        PatchZone z = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        ModulationRoute route = z.Routes.Single(r => r.Transform == ModTransform.AmplitudeCurve);
        Assert.Equal(4, route.CurveIndex);          // curve 4 (cc²), not the implicit linear
        Assert.Equal(1.0, route.Amount, 3);          // depth 100% → gain 1.0 at full curve
        Assert.IsType<ModSource.ChannelController>(route.Source);
    }

    [Fact]
    public void Gain_cc_aliases_volume_cc()
    {
        WriteWav("a.wav");
        string path = WriteSfz("<region> sample=a.wav key=60 gain_cc7=6");
        PatchZone z = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        // gain_cc is sfizz's alias for volume_oncc: +6 dB at CC7 max → an AttenuationDb route of -6.
        ModulationRoute route = z.Routes.Single(r => r.Dest == ModDestination.AttenuationDb
                                                     && r.Source is ModSource.ChannelController c && c.Number == 7);
        Assert.Equal(-6.0, route.Amount, 3);
        Assert.Equal(ModTransform.Linear, route.Transform);
    }

    [Fact]
    public void Voice_off_opcodes_parse_and_off_time_implies_time_mode()
    {
        WriteWav("a.wav");
        string path = WriteSfz("""
                               <region> sample=a.wav key=60 note_polyphony=1 off_time=0.5
                               """);
        PatchZone zone = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        Assert.True(zone.SmoothVoiceOff);
        Assert.Equal(ZoneOffMode.Time, zone.OffMode);   // off_time present, off_mode absent → Time
        Assert.Equal(0.5, zone.OffTimeSeconds);

        // A plain region keeps the hard-kill default.
        WriteWav("b.wav");
        PatchZone plain = SoundBankLoader.Load(WriteNamed("plain.sfz", "<region> sample=b.wav key=60")).FindPatch(0, 0)!.Zones[0];
        Assert.False(plain.SmoothVoiceOff);
    }

    [Fact]
    public void Humanization_opcodes_are_parsed_onto_the_zone()
    {
        WriteWav("a.wav");
        string path = WriteSfz("""
                               <region> sample=a.wav key=60
                                   amp_random=3 pitch_random=20 delay=0.01 delay_random=0.02 offset_random=500
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone zone = bank.FindPatch(0, 0)!.Zones[0];
        Assert.Equal(3.0, zone.AmpRandomDb);
        Assert.Equal(20.0, zone.PitchRandomCents);
        Assert.Equal(0.01, zone.DelaySeconds);
        Assert.Equal(0.02, zone.DelayRandomSeconds);
        Assert.Equal(500L, zone.OffsetRandomFrames);
    }

    [Fact]
    public void Trigger_release_is_parsed_and_distinguished_from_attack()
    {
        WriteWav("hit.wav");
        WriteWav("rel.wav");
        string path = WriteSfz("""
                               <region> sample=hit.wav key=60
                               <region> sample=rel.wav key=60 trigger=release rt_decay=2
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        Patch patch = bank.FindPatch(0, 0)!;
        Assert.Equal(2, patch.Zones.Count);

        // The attack region defaults to Attack; the release region carries Trigger=Release + rt_decay,
        // so NoteOn (which skips Release zones) plays only the attack one.
        PatchZone rel = Assert.Single(patch.Zones, z => z.Trigger == ZoneTrigger.Release);
        Assert.Equal(2.0, rel.RtDecay);
        Assert.Single(patch.Zones, z => z.Trigger == ZoneTrigger.Attack);
    }

    [Fact]
    public void Positional_default_path_applies_per_region()
    {
        WriteWav("A/x.wav");
        WriteWav("B/y.wav");
        string path = WriteSfz("""
                               <control> default_path=A/
                               <region> sample=x.wav lokey=0 hikey=0
                               <control> default_path=B/
                               <region> sample=y.wav lokey=1 hikey=1
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        // x.wav resolves under A/, y.wav under B/ — both found. (A single global default_path,
        // last-one-wins, would leave x.wav under B/ and unresolvable.)
        Assert.Equal(2, bank.Samples.Count);
    }

    [Fact]
    public void Inline_define_with_same_line_include_substitutes()
    {
        WriteWav("36.wav");
        WriteNamed("frag.txt", "sample=$KEY.wav\n");
        // The #define and the #include are on the same line — the include must see $KEY=36.
        string path = WriteSfz("""
                               <region> #define $KEY 36 lokey=36 hikey=36 #include "frag.txt"
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        Assert.Equal(1, bank.Samples.Count);
    }

    [Fact]
    public void Builtin_generator_loads_as_silent_placeholder()
    {
        string path = WriteSfz("<region> sample=*sine lokey=60 hikey=60");

        using IRBank bank = SoundBankLoader.Load(path);
        Assert.Equal(1, bank.Samples.Count);   // *sine registers a placeholder instead of being dropped

        var buf = new float[64];
        var n = 0;
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (n == 0 && DateTime.UtcNow < deadline)
        {
            n = bank.Samples.ReadFrames(0, 0, buf);
            if (n == 0) Thread.Sleep(5);
        }
        Assert.True(n > 0);
        for (var i = 0; i < n; i++) Assert.Equal(0f, buf[i]);   // silent placeholder
    }

    [Fact]
    public void Master_label_names_the_patch()
    {
        WriteWav("a.wav");
        string path = WriteSfz("""
                               <master> loprog=5 hiprog=5 master_label=My Program
                               <region> sample=a.wav
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        Patch? patch = bank.FindPatch(0, 5);
        Assert.NotNull(patch);
        Assert.Equal("My Program", patch!.Name);
    }

    [Fact]
    public void Loads_instrument_with_replicated_programs_and_deduped_samples()
    {
        WriteWav("samples/a.wav");
        WriteWav("samples/b.wav");
        string path = WriteSfz("""
                               <control> default_path=samples/
                               <group>
                               <region> sample=a.wav lokey=0 hikey=59
                               <region> sample=b.wav lokey=60 hikey=127
                               <region> sample=a.wav lokey=0 hikey=127
                               """);

        using IRBank bank = SoundBankLoader.Load(path);

        Assert.Equal(SoundBankFormat.Sfz, bank.SourceFormat);
        Assert.Equal(128, bank.Patches.Count);    // one instrument, all programs
        Assert.Equal(2, bank.Samples.Count);       // a.wav referenced twice → deduped
        Assert.Equal(3, ZonesOf(bank).Length);
    }

    [Fact]
    public void OctaveOffset_shifts_all_key_opcodes()
    {
        WriteWav("c.wav");
        string path = WriteSfz("""
                               <control> octave_offset=-1
                               <region> sample=c.wav lokey=60 hikey=72 pitch_keycenter=60
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone zone = ZonesOf(bank).Single();

        Assert.Equal(48, zone.Keys.Low);                    // 60 - 12
        Assert.Equal(60, zone.Keys.High);                   // 72 - 12
        Assert.Equal(48, zone.Sample.OverridingRootKey);    // keycenter 60 - 12
    }

    [Fact]
    public void Group_opcodes_cascade_but_region_overrides()
    {
        WriteWav("a.wav");
        WriteWav("b.wav");
        string path = WriteSfz("""
                               <group> ampeg_release=0.5 volume=-6
                               <region> sample=a.wav
                               <region> sample=b.wav volume=0
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone[] zones = ZonesOf(bank);

        // ampeg_release cascades to both.
        Assert.All(zones, z => Assert.Equal(0.5, z.VolumeEnvelope.ReleaseSeconds, 3));
        // volume: group -6 dB → +6 dB attenuation; region override 0 → 0 attenuation.
        Assert.Equal(6.0, zones[0].Level.AttenuationDb, 3);
        Assert.Equal(0.0, zones[1].Level.AttenuationDb, 3);
    }

    [Fact]
    public void Pan_and_loop_and_effects_translate()
    {
        WriteWav("a.wav", frames: 512, loop: (100, 400));
        string path = WriteSfz("""
                               <region> sample=a.wav pan=50 loop_mode=loop_continuous effect1=25 effect2=40
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone z = ZonesOf(bank).Single();

        Assert.Equal(0.5, z.Level.Pan, 3);
        Assert.Equal(LoopMode.Continuous, z.Sample.LoopMode);
        Assert.Equal(0.25, z.ReverbSend, 3);
        Assert.Equal(0.40, z.ChorusSend, 3);
    }

    [Fact]
    public void Loop_defaults_to_continuous_when_wav_has_smpl_loop()
    {
        WriteWav("a.wav", frames: 512, loop: (100, 400));
        string path = WriteSfz("<region> sample=a.wav");  // no loop_mode opcode

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone z = ZonesOf(bank).Single();

        Assert.Equal(LoopMode.Continuous, z.Sample.LoopMode);
        Assert.Equal(100, z.Sample.LoopStartOffset);
        Assert.Equal(401, z.Sample.LoopEndOffset);  // smpl end is inclusive → +1
    }

    [Fact]
    public void AmpVeltrack_shapes_the_velocity_gain_curve()
    {
        WriteWav("a.wav");
        WriteWav("b.wav");
        string withTrack = WriteSfz("<region> sample=a.wav amp_veltrack=100");
        using (IRBank bank = SoundBankLoader.Load(withTrack))
        {
            PatchZone z = ZonesOf(bank).Single();
            // Full veltrack → sfizz vel² curve: silent at velocity 0, full at 127.
            Assert.NotNull(z.AmpVelCurve);
            Assert.Equal(0.0, z.AmpVelCurve![0], 3);
            Assert.Equal(1.0, z.AmpVelCurve[127], 3);
        }

        // amp_veltrack=0 → velocity has no amplitude effect → flat unity curve.
        File.Delete(Path.Combine(_dir, "instrument.sfz"));
        string noTrack = WriteSfz("<region> sample=b.wav amp_veltrack=0");
        using (IRBank bank = SoundBankLoader.Load(noTrack))
        {
            PatchZone z = ZonesOf(bank).Single();
            Assert.NotNull(z.AmpVelCurve);
            Assert.Equal(1.0, z.AmpVelCurve![0], 3);
            Assert.Equal(1.0, z.AmpVelCurve[127], 3);
        }
    }

    [Fact]
    public void Default_midi_routes_are_present_on_every_zone()
    {
        WriteWav("a.wav");
        string path = WriteSfz("<region> sample=a.wav");

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone z = ZonesOf(bank).Single();

        Assert.Contains(z.Routes, r => r.Source is ModSource.ChannelController { Number: 7 });
        Assert.Contains(z.Routes, r => r.Source is ModSource.ChannelController { Number: 11 });
        Assert.Contains(z.Routes, r => r.Source is ModSource.ChannelController { Number: 10 });
        Assert.Contains(z.Routes, r => r.Source is ModSource.PitchBend);
    }

    [Fact]
    public void Comments_and_spaced_sample_paths_and_cc_gates_parse()
    {
        WriteWav("My Sample.wav");
        string path = WriteSfz("""
                               // line comment
                               <region>  /* block comment */  sample=My Sample.wav  locc64=64 hicc64=127
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone z = ZonesOf(bank).Single();

        Assert.Equal(1, bank.Samples.Count);            // the spaced path resolved
        CCGate gate = Assert.Single(z.CCGates);
        Assert.Equal(64, gate.Controller);
        Assert.Equal(64, gate.Low);
        Assert.Equal(127, gate.High);
    }

    [Fact]
    public void Missing_samples_throw_with_a_descriptive_message()
    {
        string path = WriteSfz("<region> sample=does_not_exist.wav");
        var ex = Assert.Throws<SoundBankLoadException>(() => SoundBankLoader.Load(path));
        Assert.Contains("no playable regions", ex.Message);
    }

    [Fact]
    public void LoProg_hiProg_route_each_program_to_its_own_instrument()
    {
        WriteWav("a.wav");
        WriteWav("b.wav");
        WriteWav("c.wav");
        string path = WriteSfz("""
                               <region> sample=a.wav loprog=0 hiprog=0
                               <region> sample=b.wav loprog=1 hiprog=1
                               <region> sample=c.wav
                               """);

        using IRBank bank = SoundBankLoader.Load(path);

        // Program 0 → a + c (c has no loprog → all programs); program 1 → b + c.
        Assert.Equal(2, bank.FindPatch(0, 0)!.Zones.Count);
        Assert.Equal(2, bank.FindPatch(0, 1)!.Zones.Count);
        // A program neither instrument targets still gets the unscoped c.wav zone.
        Assert.Single(bank.FindPatch(0, 64)!.Zones);
    }

    [Fact]
    public void Inline_include_pulls_regions_under_the_masters_program()
    {
        WriteWav("a.wav");
        WriteNamed("piano.sfz", "<region> sample=a.wav");
        // #include tacked onto the end of a <master> line (Discord-GM style).
        string path = WriteSfz("""<master> loprog=5 hiprog=5 #include "piano.sfz" """);

        using IRBank bank = SoundBankLoader.Load(path);

        Assert.Single(bank.FindPatch(0, 5)!.Zones);  // master's program got it
        Assert.Null(bank.FindPatch(0, 6));            // nothing elsewhere
    }

    [Fact]
    public void OnCc_pan_recenters_and_replaces_default_pan_route()
    {
        WriteWav("a.wav");
        // The Discord-GM idiom: base pan hard-left, recentered by CC10.
        string path = WriteSfz("<region> sample=a.wav pan=-100 pan_oncc10=200 amp_veltrack=0 amp_veltrack_oncc118=100");

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone z = ZonesOf(bank).Single();

        Assert.Equal(-1.0, z.Level.Pan, 3);  // base stays hard-left; the route re-centers at runtime
        ModulationRoute[] panRoutes = z.Routes.Where(r => r.Dest == ModDestination.PanNormalized).ToArray();
        Assert.Single(panRoutes);                                   // default CC10 route suppressed, only _oncc remains
        Assert.Equal(2.0, panRoutes[0].Amount, 3);                  // 200% → ±2.0 span, linear
        Assert.Equal(ModTransform.Linear, panRoutes[0].Transform);
        // amp_veltrack=0 but driven by (unseeded) CC118 → legacy fallback keeps the depth, so the
        // synthesized velocity curve still tracks (gain < 1 at velocity 0).
        Assert.NotNull(z.AmpVelCurve);
        Assert.True(z.AmpVelCurve![0] < 0.9);
    }

    [Fact]
    public void Seq_opcodes_emit_round_robin()
    {
        WriteWav("a.wav");
        WriteWav("b.wav");
        string path = WriteSfz("""
                               <region> sample=a.wav seq_length=2 seq_position=1
                               <region> sample=b.wav seq_length=2 seq_position=2
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone[] zones = ZonesOf(bank);

        Assert.Equal(new RoundRobin(0, 2), zones[0].RoundRobin);   // seq_position is 1-based → 0-based
        Assert.Equal(new RoundRobin(1, 2), zones[1].RoundRobin);
    }

    [Fact]
    public void Sw_opcodes_emit_keyswitch()
    {
        WriteWav("a.wav");
        WriteWav("b.wav");
        string path = WriteSfz("""
                               <global> sw_lokey=0 sw_hikey=11 sw_default=0
                               <region> sample=a.wav sw_last=0
                               <region> sample=b.wav sw_last=1
                               """);

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone[] zones = ZonesOf(bank);

        Assert.NotNull(zones[0].KeySwitch);
        KeySwitch ks = zones[0].KeySwitch!.Value;
        Assert.Equal(0, ks.Low);
        Assert.Equal(11, ks.High);
        Assert.Equal(0, ks.SelectingKey);
        Assert.Equal(0, ks.Default);
        Assert.Equal(1, zones[1].KeySwitch!.Value.SelectingKey);
    }

    [Fact]
    public void Combined_load_places_each_file_on_its_own_bank()
    {
        WriteWav("piano.wav");
        WriteWav("kick.wav");
        string melodic = WriteNamed("melodic.sfz", "<region> sample=piano.wav");
        string drums = WriteNamed("drums.sfz", "<region> sample=kick.wav key=36");

        using IRBank bank = SoundBankLoader.LoadSfz([(melodic, 0), (drums, 128)]);

        // Melodic on bank 0, drum kit on bank 128 (where the synth routes channel 10).
        Assert.NotNull(bank.FindPatch(0, 0));
        Assert.NotNull(bank.FindPatch(128, 0));
        Assert.Equal(2, bank.Samples.Count);                 // both files pooled, ids global

        PatchZone kick = Assert.Single(bank.FindPatch(128, 0)!.Zones);
        Assert.Equal(36, kick.Keys.Low);
        Assert.Equal(36, kick.Keys.High);                    // melodic (0-127) didn't leak onto bank 128
    }

    [Fact]
    public void Lorand_hirand_emit_random_range()
    {
        WriteWav("a.wav");
        string path = WriteSfz("<region> sample=a.wav lorand=0.25 hirand=0.75");

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone z = ZonesOf(bank).Single();

        Assert.Equal(new RandomRange(0.25, 0.75), z.Random);
    }

    [Fact]
    public void AmpVelcurve_builds_table_and_suppresses_velocity_route()
    {
        WriteWav("a.wav");
        // One interior point: 64→0.5, with implied anchors 0→0 and 127→1.
        string path = WriteSfz("<region> sample=a.wav amp_velcurve_064=0.5");

        using IRBank bank = SoundBankLoader.Load(path);
        PatchZone z = ZonesOf(bank).Single();

        Assert.NotNull(z.AmpVelCurve);
        Assert.Equal(128, z.AmpVelCurve!.Length);
        Assert.Equal(0.0, z.AmpVelCurve[0], 4);
        Assert.Equal(0.5, z.AmpVelCurve[64], 4);
        Assert.Equal(1.0, z.AmpVelCurve[127], 4);
        Assert.Equal(0.25, z.AmpVelCurve[32], 4);   // interpolated halfway from 0→64
        Assert.True(z.AmpVelCurve[96] > 0.5 && z.AmpVelCurve[96] < 1.0);  // monotonic up from 64→127
        // The default velocity→attenuation route is replaced by the curve.
        Assert.DoesNotContain(z.Routes, r => r.Source is ModSource.Velocity && r.Dest == ModDestination.AttenuationDb);
    }

    [Fact]
    public void Diagnostics_reports_unsupported_opcodes_and_ignored_headers_but_not_handled_ones()
    {
        string path = WriteSfz("""
                               <control> set_cc7=100 default_path=samples/
                               <effect> type=reverb bus=main
                               <region> sample=a.wav volume=-3 lorand=0.0 hirand=0.5 off_mode=fast amp_random=3 direction=reverse loop_crossfade=0.1
                               """);

        SfzLoadReport report = SfzDiagnostics.Scan(path);

        Assert.Equal(1, report.RegionCount);
        List<string> ops = report.UnsupportedOpcodes.Select(o => o.Opcode).ToList();
        // Genuinely-dropped opcodes are reported (numbered ones aggregated to a family).
        Assert.Contains("direction", ops);
        Assert.Contains("loop_crossfade", ops);
        // Handled opcodes — including ones implemented this session — are NOT reported.
        Assert.DoesNotContain("volume", ops);
        Assert.DoesNotContain("lorand", ops);
        Assert.DoesNotContain("hirand", ops);
        Assert.DoesNotContain("sample", ops);
        Assert.DoesNotContain("default_path", ops);
        Assert.DoesNotContain("off_mode", ops);     // Tier B
        Assert.DoesNotContain("amp_random", ops);    // Tier A
        Assert.DoesNotContain("set_ccN", ops);       // set_cc/set_hdcc seeds
        // A skipped header (e.g. <effect>) is recorded; <curve> is now parsed, not ignored.
        Assert.Contains(report.IgnoredHeaders, h => h.Header == "effect");
        // A known ARIA opcode carries an explanatory note.
        Assert.Contains(report.UnsupportedOpcodes, o => o.Opcode == "direction" && o.Note != null);
    }

    [Fact]
    public void Diagnostics_clean_font_has_no_findings()
    {
        string path = WriteSfz("<region> sample=a.wav volume=-3 ampeg_release=0.4 amp_velcurve_064=0.5");

        SfzLoadReport report = SfzDiagnostics.Scan(path);

        Assert.False(report.HasFindings);
    }

    /// <summary>
    /// Integration test against the real Clavecin harpsichord, if it has been
    /// cloned locally. No-ops otherwise so CI without the asset stays green.
    /// </summary>
    [Fact]
    public void Real_clavecin_loads_with_twelve_multisamples()
    {
        string clavecin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "soundfonts", "sfz", "Clavecin", "Clavecin.sfz");
        if (!File.Exists(clavecin)) return;  // asset not present — skip

        using IRBank bank = SoundBankLoader.Load(clavecin);
        Assert.Equal(12, bank.Samples.Count);
        PatchZone[] zones = ZonesOf(bank);
        Assert.Equal(12, zones.Length);
        // octave_offset=-1: the lowest region (written lokey=36) starts at 24.
        Assert.Contains(zones, z => z.Keys.Low == 24);
        // Group ampeg_release=0.4 cascades to every region.
        Assert.All(zones, z => Assert.Equal(0.4, z.VolumeEnvelope.ReleaseSeconds, 2));
    }

    /// <summary>
    /// Integration test against the Discord GM bank, if cloned locally. Exercises
    /// the multi-file machinery (inline #include, loprog/hiprog, _oncc) and — the
    /// point here — the FLAC decoder end-to-end: programs 104 (Sitar) and 111
    /// (Shanai) are FLAC and only load if the from-scratch decoder works.
    /// </summary>
    [Fact]
    public void Real_discord_gm_loads_flac_instruments_per_program()
    {
        string melodic = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "soundfonts", "sfz", "Discord-SFZ-GM-Bank", "Discord GM", "Discord GM Bank - Melodic.sfz");
        if (!File.Exists(melodic)) return;  // asset not present — skip

        using IRBank bank = SoundBankLoader.Load(melodic);

        // Program 0 = Salamander Grand (WAV); 104 = Sitar, 111 = Shanai (FLAC).
        Assert.NotNull(bank.FindPatch(0, 0));
        Assert.NotNull(bank.FindPatch(0, 104));   // FLAC instrument resolved
        Assert.NotNull(bank.FindPatch(0, 111));   // FLAC instrument resolved
        // A program the bank doesn't wire up has no patch.
        Assert.Null(bank.FindPatch(0, 60));
    }

    // ── Final singletons (tail of the opcode-coverage sweep) ────────────

    [Fact]
    public void Pitch_veltrack_sets_velocity_to_pitch_cents()
    {
        WriteWav("a.wav");
        PatchZone z = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 pitch_veltrack=8"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.Equal(8.0, z.PitchVelTrackCents, 3);
        PatchZone def = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.Equal(0.0, def.PitchVelTrackCents, 3);   // default = none
    }

    [Fact]
    public void Offset_oncc_collects_cc_to_start_offset()
    {
        WriteWav("a.wav");
        PatchZone z = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 offset_oncc98=320"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.NotNull(z.OffsetCc);
        Assert.Contains(z.OffsetCc!, c => c.Cc == 98 && Math.Abs(c.Amount - 320) < 1e-6);
        PatchZone def = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.Null(def.OffsetCc);   // default = none
    }

    [Fact]
    public void Polyphony_sets_region_voice_cap()
    {
        WriteWav("a.wav");
        PatchZone z = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 polyphony=1"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.Equal(1, z.Polyphony);
        PatchZone def = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.Equal(-1, def.Polyphony);   // default = unlimited
    }

    [Fact]
    public void Sustain_cc_reassigns_bank_sustain_controller()
    {
        WriteWav("a.wav");
        IRBank bank = SoundBankLoader.Load(WriteSfz("<global> sustain_cc=90\n<region> sample=a.wav key=60"));
        Assert.Equal(90, bank.SustainCc);
        Assert.Equal(90, bank.FindPatch(0, 0)!.Zones[0].SustainCc);
        IRBank def = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60"));
        Assert.Equal(64, def.SustainCc);   // default = CC64
    }

    [Fact]
    public void Ampeg_release_shape_parses_into_volume_envelope()
    {
        WriteWav("a.wav");
        PatchZone z = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 ampeg_release=0.5 ampeg_release_shape=-6"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.Equal(-6.0, z.VolumeEnvelope.ReleaseShape, 3);
        PatchZone def = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.Equal(0.0, def.VolumeEnvelope.ReleaseShape, 3);   // default = unshaped exponential
    }

    [Fact]
    public void Stepped_lfo_parses_step_table()
    {
        WriteWav("a.wav");
        GenericLfo lfo = SoundBankLoader.Load(WriteSfz(
                "<region> sample=a.wav key=60 lfo1_freq=4 lfo1_wave=13 lfo1_volume=6 " +
                "lfo1_steps=4 lfo1_step1=100 lfo1_step2=50 lfo1_step3=-50 lfo1_step4=-100"))
            .FindPatch(0, 0)!.Zones[0].Lfos![0];
        double[]? steps = lfo.Stages[0].Steps;
        Assert.NotNull(steps);
        Assert.Equal(new[] { 1.0, 0.5, -0.5, -1.0 }, steps!);

        // A non-stepped LFO carries no step table.
        GenericLfo plain = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 lfo1_freq=4 lfo1_wave=1 lfo1_pitch=50"))
            .FindPatch(0, 0)!.Zones[0].Lfos![0];
        Assert.Null(plain.Stages[0].Steps);
    }

    [Fact]
    public void Shelf_and_peaking_filter_types_carry_fil_gain()
    {
        WriteWav("a.wav");
        FilterSettings lsh = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 fil_type=lsh cutoff=200 fil_gain=6"))
            .FindPatch(0, 0)!.Zones[0].Filter!;
        Assert.Equal(FilterType.LowShelf, lsh.Type);
        Assert.Equal(6.0, lsh.GainDb, 3);

        FilterSettings hsh = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 fil_type=hsh cutoff=4000 fil_gain=-3"))
            .FindPatch(0, 0)!.Zones[0].Filter!;
        Assert.Equal(FilterType.HighShelf, hsh.Type);
        Assert.Equal(-3.0, hsh.GainDb, 3);

        FilterSettings peq = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 fil_type=peq cutoff=1000 fil_gain=9"))
            .FindPatch(0, 0)!.Zones[0].Filter!;
        Assert.Equal(FilterType.Peaking, peq.Type);
        Assert.Equal(9.0, peq.GainDb, 3);

        // Second filter honours fil2_gain too.
        FilterSettings f2 = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 cutoff2=3000 fil2_type=hsh fil2_gain=4"))
            .FindPatch(0, 0)!.Zones[0].Filter2!;
        Assert.Equal(FilterType.HighShelf, f2.Type);
        Assert.Equal(4.0, f2.GainDb, 3);

        // A pass-type filter leaves gain at 0 (the default).
        FilterSettings lpf = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 cutoff=800"))
            .FindPatch(0, 0)!.Zones[0].Filter!;
        Assert.Equal(0.0, lpf.GainDb, 3);
    }

    [Fact]
    public void Eq_oncc_builds_live_cc_modulation_on_a_band()
    {
        WriteWav("a.wav");
        // A band with 0 base gain that exists only to be CC-driven (eq1_gain_oncc), plus freq/bw CC.
        PatchZone z = SoundBankLoader.Load(WriteSfz(
                "<region> sample=a.wav key=60 eq1_freq=200 eq1_bw=0 " +
                "eq1_gain_oncc86=12 eq1_freq_oncc80=380 eq1_bw_oncc92=4"))
            .FindPatch(0, 0)!.Zones[0];
        Assert.Single(z.EqBands);
        EqBand band = z.EqBands[0];
        Assert.Equal(1, band.BandNumber);
        Assert.NotNull(band.GainCc);
        Assert.Contains(band.GainCc!, c => c.Cc == 86 && Math.Abs(c.Amount - 12) < 1e-6);
        Assert.NotNull(band.FreqCc);
        Assert.Contains(band.FreqCc!, c => c.Cc == 80 && Math.Abs(c.Amount - 380) < 1e-6);
        Assert.NotNull(band.BwCc);
        Assert.Contains(band.BwCc!, c => c.Cc == 92 && Math.Abs(c.Amount - 4) < 1e-6);
    }
}
