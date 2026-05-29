using System;
using System.IO;
using System.Linq;
using System.Text;
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
    public void AmpVeltrack_controls_velocity_route_emission()
    {
        WriteWav("a.wav");
        WriteWav("b.wav");
        var withTrack = WriteSfz("<region> sample=a.wav amp_veltrack=100");
        using (var bank = SoundBankLoader.Load(withTrack))
        {
            var z = ZonesOf(bank).Single();
            Assert.Contains(z.Routes, r => r.Source is ModSource.Velocity && r.Dest == ModDestination.AttenuationDb);
        }

        // amp_veltrack=0 → velocity has no amplitude effect → no velocity route.
        File.Delete(Path.Combine(_dir, "instrument.sfz"));
        var noTrack = WriteSfz("<region> sample=b.wav amp_veltrack=0");
        using (var bank = SoundBankLoader.Load(noTrack))
        {
            var z = ZonesOf(bank).Single();
            Assert.DoesNotContain(z.Routes, r => r.Source is ModSource.Velocity);
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
        // amp_veltrack=0 but driven by CC118 → velocity route restored.
        Assert.Contains(z.Routes, r => r.Source is ModSource.Velocity);
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
