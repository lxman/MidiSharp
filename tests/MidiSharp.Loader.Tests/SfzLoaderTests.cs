using System;
using System.IO;
using System.Linq;
using System.Text;
using Loader;
using Loader.Sfz;
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
        using var fs = File.Create(full);
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
            for (int i = 0; i < 7; i++) w.Write(0);  // manuf..smpteOffset
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
        for (int i = 0; i < frames; i++)
            w.Write((short)(i % 64 * 32));  // tiny ramp, well below clipping
    }

    private static PatchZone[] ZonesOf(IRBank bank)
    {
        var patch = bank.FindPatch(0, 0);
        Assert.NotNull(patch);
        return patch!.Zones.ToArray();
    }

    // ── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public void LazySample_decodes_in_background_then_reads_correct_data()
    {
        WriteWav("samples/a.wav", frames: 256);
        var path = WriteSfz("""
            <control> default_path=samples/
            <region> sample=a.wav lokey=0 hikey=127
            """);

        using var bank = SoundBankLoader.Load(path);
        Assert.True(bank.Samples.Count >= 1);

        // Lazy: the first read kicks a background decode and returns silence (0); poll until it lands.
        var buf = new float[256];
        int n = 0;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (n == 0 && DateTime.UtcNow < deadline)
        {
            n = bank.Samples.ReadFrames(0, 0, buf);
            if (n == 0) System.Threading.Thread.Sleep(5);
        }

        Assert.True(n > 0, "background decode never produced data");
        // WriteWav lays down a known ramp: sample i = (i % 64 * 32) as int16, normalised by 1/32768.
        for (int i = 0; i < n; i++)
            Assert.Equal((short)(i % 64 * 32) / 32768.0, buf[i], 5);
    }

    [Fact]
    public void BlockingSampleDecode_returns_data_on_the_first_read()
    {
        WriteWav("samples/a.wav", frames: 256);
        var path = WriteSfz("""
            <control> default_path=samples/
            <region> sample=a.wav lokey=0 hikey=127
            """);

        // Offline-render mode: the first ReadFrames must decode synchronously and return the sample
        // data — no background race, no transient silence. (The lazy default returns 0 on the first
        // call; see LazySample_decodes_in_background_then_reads_correct_data.) This guards the fix for
        // fast first-hit notes losing their attack in a WAV export.
        using var bank = SoundBankLoader.Load(path, new SoundBankLoadOptions { BlockingSampleDecode = true });

        var buf = new float[256];
        int n = bank.Samples.ReadFrames(0, 0, buf);   // single call, no polling

        Assert.True(n > 0, "blocking decode should return data on the first read");
        for (int i = 0; i < n; i++)
            Assert.Equal((short)(i % 64 * 32) / 32768.0, buf[i], 5);
    }

    [Fact]
    public void Amp_veltrack_oncc_curve_reduces_velocity_tracking()
    {
        WriteWav("a.wav");
        var path = WriteSfz("""
            <control> set_hdcc99=0.73
            <region> sample=a.wav key=60 amp_veltrack_oncc99=-100 amp_veltrack_curvecc99=2
            """);
        var zone = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
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
        var path = WriteSfz("""
            <control> set_cc7=100 set_hdcc99=0.73
            <region> sample=a.wav key=60
            """);
        using var bank = SoundBankLoader.Load(path);
        Assert.Equal(100, bank.InitialControllers[7]);
        Assert.Equal(93, bank.InitialControllers[99]);   // 0.73 × 127 = 92.71 → 93
    }

    [Fact]
    public void Ampeg_release_oncc_bakes_into_release_at_seeded_cc()
    {
        WriteWav("a.wav");
        // cc72 seeded to 0.5 (≈64); ampeg_release_oncc72=2 with the default linear curve → +2×0.5 ≈ 1 s.
        var path = WriteSfz("""
            <control> set_hdcc72=0.5
            <region> sample=a.wav key=60 ampeg_release_oncc72=2
            """);
        var ve = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0].VolumeEnvelope;
        Assert.InRange(ve.ReleaseSeconds, 0.95, 1.05);
    }

    [Fact]
    public void Ampeg_attack_bare_cc_alias_bakes_like_oncc()
    {
        WriteWav("a.wav");
        // v1/ARIA short form: ampeg_attackcc72 (no underscore) is an alias of ampeg_attack_oncc72.
        // cc72 seeded to 0.5 (≈64), linear curve → +2×0.5 ≈ 1 s on top of the 0.1 s base attack.
        var path = WriteSfz("""
            <control> set_hdcc72=0.5
            <region> sample=a.wav key=60 ampeg_attack=0.1 ampeg_attackcc72=2
            """);
        var ve = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0].VolumeEnvelope;
        Assert.InRange(ve.AttackSeconds, 1.05, 1.15);
    }

    [Fact]
    public void Ampeg_vel2_envelope_modulation_parses()
    {
        WriteWav("a.wav");
        var path = WriteSfz("<region> sample=a.wav key=60 ampeg_attack=0.5 ampeg_vel2attack=-0.4 ampeg_vel2decay=0.2 ampeg_vel2sustain=-50");
        var ve = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0].VolumeEnvelope;
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
        var path = WriteSfz("<region> sample=a.wav key=60 amp_veltrack=0 xfin_lovel=0 xfin_hivel=64 xfout_lovel=64 xfout_hivel=127");
        var c = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0].AmpVelCurve;
        Assert.NotNull(c);
        Assert.Equal(0.0, c![0], 2);     // faded out at the bottom
        Assert.True(c[64] > 0.9);        // ~peak at the crossover
        Assert.Equal(0.0, c[127], 2);    // faded out at the top
    }

    [Fact]
    public void Eq_bands_parse_and_skip_zero_gain()
    {
        WriteWav("a.wav");
        var path = WriteSfz("""
            <region> sample=a.wav key=60
                eq1_freq=120 eq1_bw=2 eq1_gain=6
                eq2_freq=3000 eq2_gain=0
                eq3_freq=8000 eq3_bw=0.5 eq3_gain=-4
            """);
        var z = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
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
        var path = WriteSfz("<region> sample=a.wav key=60 cutoff=1000 fil_keytrack=100 fil_keycenter=48 fil_veltrack=2400");
        var z = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        Assert.NotNull(z.Filter);
        Assert.Equal(100.0, z.Filter!.KeyTrackCentsPerKey, 3);
        Assert.Equal(48, z.Filter.KeyTrackCenter);
        // fil_veltrack → a linear velocity→cutoff route.
        var velCut = z.Routes.Single(r => r.Source is ModSource.Velocity && r.Dest == ModDestination.FilterCutoffCents);
        Assert.Equal(2400.0, velCut.Amount, 3);
        Assert.Equal(ModTransform.Linear, velCut.Transform);
    }

    [Fact]
    public void Bend_up_sets_the_pitch_bend_route_range()
    {
        WriteWav("a.wav");
        var z = SoundBankLoader.Load(WriteSfz("<region> sample=a.wav key=60 bend_up=400"))
            .FindPatch(0, 0)!.Zones[0];
        var pb = z.Routes.Single(r => r.Source is ModSource.PitchBend && r.Dest == ModDestination.PitchCents);
        Assert.Equal(400.0, pb.Amount, 3);          // ±4 semitones
        Assert.Null(pb.AmountModulator);             // not RPN-scaled — SFZ uses bend_up directly

        WriteWav("b.wav");
        var def = SoundBankLoader.Load(WriteNamed("def.sfz", "<region> sample=b.wav key=60"))
            .FindPatch(0, 0)!.Zones[0];
        var pbd = def.Routes.Single(r => r.Source is ModSource.PitchBend);
        Assert.Equal(200.0, pbd.Amount, 3);          // default = ±2 semitones
    }

    [Fact]
    public void Amplitude_oncc_uses_the_aria_curve()
    {
        WriteWav("a.wav");
        var path = WriteSfz("<region> sample=a.wav key=60 amplitude_oncc7=100 amplitude_curvecc7=4");
        var z = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        var route = z.Routes.Single(r => r.Transform == ModTransform.AmplitudeCurve);
        Assert.Equal(4, route.CurveIndex);          // curve 4 (cc²), not the implicit linear
        Assert.Equal(1.0, route.Amount, 3);          // depth 100% → gain 1.0 at full curve
        Assert.IsType<ModSource.ChannelController>(route.Source);
    }

    [Fact]
    public void Gain_cc_aliases_volume_cc()
    {
        WriteWav("a.wav");
        var path = WriteSfz("<region> sample=a.wav key=60 gain_cc7=6");
        var z = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        // gain_cc is sfizz's alias for volume_oncc: +6 dB at CC7 max → an AttenuationDb route of -6.
        var route = z.Routes.Single(r => r.Dest == ModDestination.AttenuationDb
            && r.Source is ModSource.ChannelController c && c.Number == 7);
        Assert.Equal(-6.0, route.Amount, 3);
        Assert.Equal(ModTransform.Linear, route.Transform);
    }

    [Fact]
    public void Voice_off_opcodes_parse_and_off_time_implies_time_mode()
    {
        WriteWav("a.wav");
        var path = WriteSfz("""
            <region> sample=a.wav key=60 note_polyphony=1 off_time=0.5
            """);
        var zone = SoundBankLoader.Load(path).FindPatch(0, 0)!.Zones[0];
        Assert.True(zone.SmoothVoiceOff);
        Assert.Equal(ZoneOffMode.Time, zone.OffMode);   // off_time present, off_mode absent → Time
        Assert.Equal(0.5, zone.OffTimeSeconds);

        // A plain region keeps the hard-kill default.
        WriteWav("b.wav");
        var plain = SoundBankLoader.Load(WriteNamed("plain.sfz", "<region> sample=b.wav key=60")).FindPatch(0, 0)!.Zones[0];
        Assert.False(plain.SmoothVoiceOff);
    }

    [Fact]
    public void Humanization_opcodes_are_parsed_onto_the_zone()
    {
        WriteWav("a.wav");
        var path = WriteSfz("""
            <region> sample=a.wav key=60
                amp_random=3 pitch_random=20 delay=0.01 delay_random=0.02 offset_random=500
            """);

        using var bank = SoundBankLoader.Load(path);
        var zone = bank.FindPatch(0, 0)!.Zones[0];
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
        var path = WriteSfz("""
            <region> sample=hit.wav key=60
            <region> sample=rel.wav key=60 trigger=release rt_decay=2
            """);

        using var bank = SoundBankLoader.Load(path);
        var patch = bank.FindPatch(0, 0)!;
        Assert.Equal(2, patch.Zones.Count);

        // The attack region defaults to Attack; the release region carries Trigger=Release + rt_decay,
        // so NoteOn (which skips Release zones) plays only the attack one.
        var rel = Assert.Single(patch.Zones, z => z.Trigger == ZoneTrigger.Release);
        Assert.Equal(2.0, rel.RtDecay);
        Assert.Single(patch.Zones, z => z.Trigger == ZoneTrigger.Attack);
    }

    [Fact]
    public void Positional_default_path_applies_per_region()
    {
        WriteWav("A/x.wav");
        WriteWav("B/y.wav");
        var path = WriteSfz("""
            <control> default_path=A/
            <region> sample=x.wav lokey=0 hikey=0
            <control> default_path=B/
            <region> sample=y.wav lokey=1 hikey=1
            """);

        using var bank = SoundBankLoader.Load(path);
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
        var path = WriteSfz("""
            <region> #define $KEY 36 lokey=36 hikey=36 #include "frag.txt"
            """);

        using var bank = SoundBankLoader.Load(path);
        Assert.Equal(1, bank.Samples.Count);
    }

    [Fact]
    public void Builtin_generator_loads_as_silent_placeholder()
    {
        var path = WriteSfz("<region> sample=*sine lokey=60 hikey=60");

        using var bank = SoundBankLoader.Load(path);
        Assert.Equal(1, bank.Samples.Count);   // *sine registers a placeholder instead of being dropped

        var buf = new float[64];
        int n = 0;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (n == 0 && DateTime.UtcNow < deadline)
        {
            n = bank.Samples.ReadFrames(0, 0, buf);
            if (n == 0) System.Threading.Thread.Sleep(5);
        }
        Assert.True(n > 0);
        for (int i = 0; i < n; i++) Assert.Equal(0f, buf[i]);   // silent placeholder
    }

    [Fact]
    public void Master_label_names_the_patch()
    {
        WriteWav("a.wav");
        var path = WriteSfz("""
            <master> loprog=5 hiprog=5 master_label=My Program
            <region> sample=a.wav
            """);

        using var bank = SoundBankLoader.Load(path);
        var patch = bank.FindPatch(0, 5);
        Assert.NotNull(patch);
        Assert.Equal("My Program", patch!.Name);
    }

    [Fact]
    public void Loads_instrument_with_replicated_programs_and_deduped_samples()
    {
        WriteWav("samples/a.wav");
        WriteWav("samples/b.wav");
        var path = WriteSfz("""
            <control> default_path=samples/
            <group>
            <region> sample=a.wav lokey=0 hikey=59
            <region> sample=b.wav lokey=60 hikey=127
            <region> sample=a.wav lokey=0 hikey=127
            """);

        using var bank = SoundBankLoader.Load(path);

        Assert.Equal(SoundBankFormat.Sfz, bank.SourceFormat);
        Assert.Equal(128, bank.Patches.Count);    // one instrument, all programs
        Assert.Equal(2, bank.Samples.Count);       // a.wav referenced twice → deduped
        Assert.Equal(3, ZonesOf(bank).Length);
    }

    [Fact]
    public void OctaveOffset_shifts_all_key_opcodes()
    {
        WriteWav("c.wav");
        var path = WriteSfz("""
            <control> octave_offset=-1
            <region> sample=c.wav lokey=60 hikey=72 pitch_keycenter=60
            """);

        using var bank = SoundBankLoader.Load(path);
        var zone = ZonesOf(bank).Single();

        Assert.Equal(48, zone.Keys.Low);                    // 60 - 12
        Assert.Equal(60, zone.Keys.High);                   // 72 - 12
        Assert.Equal(48, zone.Sample.OverridingRootKey);    // keycenter 60 - 12
    }

    [Fact]
    public void Group_opcodes_cascade_but_region_overrides()
    {
        WriteWav("a.wav");
        WriteWav("b.wav");
        var path = WriteSfz("""
            <group> ampeg_release=0.5 volume=-6
            <region> sample=a.wav
            <region> sample=b.wav volume=0
            """);

        using var bank = SoundBankLoader.Load(path);
        var zones = ZonesOf(bank);

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
        var path = WriteSfz("""
            <region> sample=a.wav pan=50 loop_mode=loop_continuous effect1=25 effect2=40
            """);

        using var bank = SoundBankLoader.Load(path);
        var z = ZonesOf(bank).Single();

        Assert.Equal(0.5, z.Level.Pan, 3);
        Assert.Equal(LoopMode.Continuous, z.Sample.LoopMode);
        Assert.Equal(0.25, z.ReverbSend, 3);
        Assert.Equal(0.40, z.ChorusSend, 3);
    }

    [Fact]
    public void Loop_defaults_to_continuous_when_wav_has_smpl_loop()
    {
        WriteWav("a.wav", frames: 512, loop: (100, 400));
        var path = WriteSfz("<region> sample=a.wav");  // no loop_mode opcode

        using var bank = SoundBankLoader.Load(path);
        var z = ZonesOf(bank).Single();

        Assert.Equal(LoopMode.Continuous, z.Sample.LoopMode);
        Assert.Equal(100, z.Sample.LoopStartOffset);
        Assert.Equal(401, z.Sample.LoopEndOffset);  // smpl end is inclusive → +1
    }

    [Fact]
    public void AmpVeltrack_shapes_the_velocity_gain_curve()
    {
        WriteWav("a.wav");
        WriteWav("b.wav");
        var withTrack = WriteSfz("<region> sample=a.wav amp_veltrack=100");
        using (var bank = SoundBankLoader.Load(withTrack))
        {
            var z = ZonesOf(bank).Single();
            // Full veltrack → sfizz vel² curve: silent at velocity 0, full at 127.
            Assert.NotNull(z.AmpVelCurve);
            Assert.Equal(0.0, z.AmpVelCurve![0], 3);
            Assert.Equal(1.0, z.AmpVelCurve[127], 3);
        }

        // amp_veltrack=0 → velocity has no amplitude effect → flat unity curve.
        File.Delete(Path.Combine(_dir, "instrument.sfz"));
        var noTrack = WriteSfz("<region> sample=b.wav amp_veltrack=0");
        using (var bank = SoundBankLoader.Load(noTrack))
        {
            var z = ZonesOf(bank).Single();
            Assert.NotNull(z.AmpVelCurve);
            Assert.Equal(1.0, z.AmpVelCurve![0], 3);
            Assert.Equal(1.0, z.AmpVelCurve[127], 3);
        }
    }

    [Fact]
    public void Default_midi_routes_are_present_on_every_zone()
    {
        WriteWav("a.wav");
        var path = WriteSfz("<region> sample=a.wav");

        using var bank = SoundBankLoader.Load(path);
        var z = ZonesOf(bank).Single();

        Assert.Contains(z.Routes, r => r.Source is ModSource.ChannelController { Number: 7 });
        Assert.Contains(z.Routes, r => r.Source is ModSource.ChannelController { Number: 11 });
        Assert.Contains(z.Routes, r => r.Source is ModSource.ChannelController { Number: 10 });
        Assert.Contains(z.Routes, r => r.Source is ModSource.PitchBend);
    }

    [Fact]
    public void Comments_and_spaced_sample_paths_and_cc_gates_parse()
    {
        WriteWav("My Sample.wav");
        var path = WriteSfz("""
            // line comment
            <region>  /* block comment */  sample=My Sample.wav  locc64=64 hicc64=127
            """);

        using var bank = SoundBankLoader.Load(path);
        var z = ZonesOf(bank).Single();

        Assert.Equal(1, bank.Samples.Count);            // the spaced path resolved
        var gate = Assert.Single(z.CCGates);
        Assert.Equal(64, gate.Controller);
        Assert.Equal(64, gate.Low);
        Assert.Equal(127, gate.High);
    }

    [Fact]
    public void Missing_samples_throw_with_a_descriptive_message()
    {
        var path = WriteSfz("<region> sample=does_not_exist.wav");
        var ex = Assert.Throws<SoundBankLoadException>(() => SoundBankLoader.Load(path));
        Assert.Contains("no playable regions", ex.Message);
    }

    [Fact]
    public void LoProg_hiProg_route_each_program_to_its_own_instrument()
    {
        WriteWav("a.wav");
        WriteWav("b.wav");
        WriteWav("c.wav");
        var path = WriteSfz("""
            <region> sample=a.wav loprog=0 hiprog=0
            <region> sample=b.wav loprog=1 hiprog=1
            <region> sample=c.wav
            """);

        using var bank = SoundBankLoader.Load(path);

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
        var path = WriteSfz("""<master> loprog=5 hiprog=5 #include "piano.sfz" """);

        using var bank = SoundBankLoader.Load(path);

        Assert.Single(bank.FindPatch(0, 5)!.Zones);  // master's program got it
        Assert.Null(bank.FindPatch(0, 6));            // nothing elsewhere
    }

    [Fact]
    public void OnCc_pan_recenters_and_replaces_default_pan_route()
    {
        WriteWav("a.wav");
        // The Discord-GM idiom: base pan hard-left, recentered by CC10.
        var path = WriteSfz("<region> sample=a.wav pan=-100 pan_oncc10=200 amp_veltrack=0 amp_veltrack_oncc118=100");

        using var bank = SoundBankLoader.Load(path);
        var z = ZonesOf(bank).Single();

        Assert.Equal(-1.0, z.Level.Pan, 3);  // base stays hard-left; the route re-centers at runtime
        var panRoutes = z.Routes.Where(r => r.Dest == ModDestination.PanNormalized).ToArray();
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
        var path = WriteSfz("""
            <region> sample=a.wav seq_length=2 seq_position=1
            <region> sample=b.wav seq_length=2 seq_position=2
            """);

        using var bank = SoundBankLoader.Load(path);
        var zones = ZonesOf(bank);

        Assert.Equal(new RoundRobin(0, 2), zones[0].RoundRobin);   // seq_position is 1-based → 0-based
        Assert.Equal(new RoundRobin(1, 2), zones[1].RoundRobin);
    }

    [Fact]
    public void Sw_opcodes_emit_keyswitch()
    {
        WriteWav("a.wav");
        WriteWav("b.wav");
        var path = WriteSfz("""
            <global> sw_lokey=0 sw_hikey=11 sw_default=0
            <region> sample=a.wav sw_last=0
            <region> sample=b.wav sw_last=1
            """);

        using var bank = SoundBankLoader.Load(path);
        var zones = ZonesOf(bank);

        Assert.NotNull(zones[0].KeySwitch);
        var ks = zones[0].KeySwitch!.Value;
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
        var melodic = WriteNamed("melodic.sfz", "<region> sample=piano.wav");
        var drums = WriteNamed("drums.sfz", "<region> sample=kick.wav key=36");

        using var bank = SoundBankLoader.LoadSfz(new[] { (melodic, 0), (drums, 128) });

        // Melodic on bank 0, drum kit on bank 128 (where the synth routes channel 10).
        Assert.NotNull(bank.FindPatch(0, 0));
        Assert.NotNull(bank.FindPatch(128, 0));
        Assert.Equal(2, bank.Samples.Count);                 // both files pooled, ids global

        var kick = Assert.Single(bank.FindPatch(128, 0)!.Zones);
        Assert.Equal(36, kick.Keys.Low);
        Assert.Equal(36, kick.Keys.High);                    // melodic (0-127) didn't leak onto bank 128
    }

    [Fact]
    public void Lorand_hirand_emit_random_range()
    {
        WriteWav("a.wav");
        var path = WriteSfz("<region> sample=a.wav lorand=0.25 hirand=0.75");

        using var bank = SoundBankLoader.Load(path);
        var z = ZonesOf(bank).Single();

        Assert.Equal(new RandomRange(0.25, 0.75), z.Random);
    }

    [Fact]
    public void AmpVelcurve_builds_table_and_suppresses_velocity_route()
    {
        WriteWav("a.wav");
        // One interior point: 64→0.5, with implied anchors 0→0 and 127→1.
        var path = WriteSfz("<region> sample=a.wav amp_velcurve_064=0.5");

        using var bank = SoundBankLoader.Load(path);
        var z = ZonesOf(bank).Single();

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
        var path = WriteSfz("""
            <control> set_cc7=100 default_path=samples/
            <curve> curve_index=1 v000=0 v127=1
            <region> sample=a.wav volume=-3 lorand=0.0 hirand=0.5 off_mode=fast amp_random=3 direction=reverse width_oncc20=50
            """);

        var report = SfzDiagnostics.Scan(path);

        Assert.Equal(1, report.RegionCount);
        var ops = report.UnsupportedOpcodes.Select(o => o.Opcode).ToList();
        // Genuinely-dropped opcodes are reported (numbered ones aggregated to a family).
        Assert.Contains("direction", ops);
        Assert.Contains("width_onccN", ops);
        // Handled opcodes — including ones implemented this session — are NOT reported.
        Assert.DoesNotContain("volume", ops);
        Assert.DoesNotContain("lorand", ops);
        Assert.DoesNotContain("hirand", ops);
        Assert.DoesNotContain("sample", ops);
        Assert.DoesNotContain("default_path", ops);
        Assert.DoesNotContain("off_mode", ops);     // Tier B
        Assert.DoesNotContain("amp_random", ops);    // Tier A
        Assert.DoesNotContain("set_ccN", ops);       // set_cc/set_hdcc seeds
        // The skipped <curve> header is recorded.
        Assert.Contains(report.IgnoredHeaders, h => h.Header == "curve");
        // A known ARIA opcode carries an explanatory note.
        Assert.Contains(report.UnsupportedOpcodes, o => o.Opcode == "direction" && o.Note != null);
    }

    [Fact]
    public void Diagnostics_clean_font_has_no_findings()
    {
        var path = WriteSfz("<region> sample=a.wav volume=-3 ampeg_release=0.4 amp_velcurve_064=0.5");

        var report = SfzDiagnostics.Scan(path);

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

        using var bank = SoundBankLoader.Load(clavecin);
        Assert.Equal(12, bank.Samples.Count);
        var zones = ZonesOf(bank);
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

        using var bank = SoundBankLoader.Load(melodic);

        // Program 0 = Salamander Grand (WAV); 104 = Sitar, 111 = Shanai (FLAC).
        Assert.NotNull(bank.FindPatch(0, 0));
        Assert.NotNull(bank.FindPatch(0, 104));   // FLAC instrument resolved
        Assert.NotNull(bank.FindPatch(0, 111));   // FLAC instrument resolved
        // A program the bank doesn't wire up has no patch.
        Assert.Null(bank.FindPatch(0, 60));
    }
}
