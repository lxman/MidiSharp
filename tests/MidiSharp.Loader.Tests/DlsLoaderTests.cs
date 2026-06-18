using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MidiSharp.Loader;
using MidiSharp.Loader.Dls;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.Loader.Tests;

/// <summary>
/// End-to-end DLS loader tests through the public <see cref="SoundBankLoader"/>. Each test synthesizes a
/// minimal in-memory DLS (RIFF "DLS " with the chunk layout <see cref="DlsReader"/> expects) and asserts
/// on the resulting IR — exercising the binary reader, the articulation translator, and the zone builder
/// together. DLS connection scales use the standard encodings (time-cents, absolute-cents, 16.16 fixed),
/// mirrored here in small helpers so the assertions prove the binary→IR wiring.
/// </summary>
public sealed class DlsLoaderTests : IDisposable
{
    private readonly string _dir;

    public DlsLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dlstest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Scale encodings (mirror the loader's conversions) ───────────────

    /// <summary>Seconds → DLS time-cents scale (16.16). sec = 2^(tc/1200).</summary>
    private static int Tc(double seconds) => (int)Math.Round(1200.0 * Math.Log2(seconds) * 65536.0);
    private static double TcToSec(int scale) { double tc = scale / 65536.0; return tc <= -12000 ? 0 : Math.Pow(2, tc / 1200.0); }

    /// <summary>Hz → DLS absolute-cents scale (16.16). Hz = 8.176 · 2^(cents/1200).</summary>
    private static int AbsCents(double hz) => (int)Math.Round(1200.0 * Math.Log2(hz / 8.176) * 65536.0);
    private static double AbsCentsToHz(int scale) => 8.176 * Math.Pow(2, (scale / 65536.0) / 1200.0);

    /// <summary>A raw value (cents / dB·10) → 16.16 scale.</summary>
    private static int Fixed16(double value) => (int)Math.Round(value * 65536.0);

    // ── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public void Loads_basic_instrument_region_and_sample()
    {
        var pcm = Enumerable.Range(0, 100).Select(i => (short)(i * 10)).ToArray();
        var dls = MakeDls(
            Instrument(bank: 0, prog: 5, drum: false, name: "Test Piano",
                instArts: null,
                regions: new[] { Region(48, 72, 0, 127, keyGroup: 0, tableIndex: 0, arts: null, rgnWsmp: null) }),
            new[] { Wave(unity: 60, fine: 0, gainCb1616: 0, pcm: pcm, loop: null) });

        using var bank = Load(dls);

        var patch = bank.FindPatch(0, 5);
        Assert.NotNull(patch);
        Assert.Equal("Test Piano", patch!.Name);
        var z = patch.Zones[0];
        Assert.Equal(48, z.Keys.Low);
        Assert.Equal(72, z.Keys.High);
        Assert.Equal(60, z.Sample.OverridingRootKey);
        Assert.Equal(100, bank.Samples.Metadata(z.Sample.SampleId).LengthFrames);
    }

    [Fact]
    public void Volume_envelope_from_eg1_connections()
    {
        var arts = Art2(
            Conn(ConnectionSource.None, ConnectionDestination.Eg1AttackTime, Tc(2.0)),
            Conn(ConnectionSource.None, ConnectionDestination.Eg1DecayTime, Tc(4.0)),
            Conn(ConnectionSource.None, ConnectionDestination.Eg1ReleaseTime, Tc(0.5)),
            Conn(ConnectionSource.None, ConnectionDestination.Eg1SustainLevel, Fixed16(SustainCb(0.5))));

        var z = LoadSingleZone(arts);
        var env = z.VolumeEnvelope;
        Assert.Equal(2.0, env.AttackSeconds, 3);
        Assert.Equal(4.0, env.DecaySeconds, 3);
        Assert.Equal(0.5, env.ReleaseSeconds, 3);
        Assert.Equal(0.5, env.SustainLevel, 2);
    }

    [Fact]
    public void Mod_envelope_eg2_targets_pitch_and_filter()
    {
        var arts = Art2(
            // EG2 timing makes the mod envelope exist; EG2 as a source supplies the depths.
            Conn(ConnectionSource.None, ConnectionDestination.Eg2AttackTime, Tc(1.0)),
            Conn(ConnectionSource.None, ConnectionDestination.Eg2ReleaseTime, Tc(0.25)),
            Conn(ConnectionSource.Eg2, ConnectionDestination.Pitch, Fixed16(300)),       // +300 cents
            Conn(ConnectionSource.Eg2, ConnectionDestination.FilterCutoff, Fixed16(1200)));

        var z = LoadSingleZone(arts);
        Assert.NotNull(z.ModulationEnvelope);
        Assert.Equal(1.0, z.ModulationEnvelope!.AttackSeconds, 3);
        Assert.Equal(300.0, z.Pitch.ModulationEnvelopeDepthCents, 1);
        Assert.NotNull(z.Filter);
        Assert.Equal(1200.0, z.Filter!.EnvelopeDepthCents, 1);
    }

    [Fact]
    public void Filter_cutoff_and_q_from_connections()
    {
        int cutoffScale = AbsCents(2000);
        var arts = Art2(
            Conn(ConnectionSource.None, ConnectionDestination.FilterCutoff, cutoffScale),
            Conn(ConnectionSource.None, ConnectionDestination.FilterQ, Fixed16(120)));   // 12.0 dB (cB→dB)

        var z = LoadSingleZone(arts);
        Assert.NotNull(z.Filter);
        Assert.Equal(FilterType.LowPass, z.Filter!.Type);
        Assert.Equal(AbsCentsToHz(cutoffScale), z.Filter.CutoffHz, 1);
        Assert.Equal(12.0, z.Filter.ResonanceDb, 2);
    }

    [Fact]
    public void Vibrato_lfo_from_connections()
    {
        int freqScale = AbsCents(6.0);   // 6 Hz
        var arts = Art2(
            Conn(ConnectionSource.None, ConnectionDestination.VibratoFrequency, freqScale),
            Conn(ConnectionSource.None, ConnectionDestination.VibratoStartDelay, Tc(0.5)),
            Conn(ConnectionSource.Vibrato, ConnectionDestination.Pitch, Fixed16(40)));   // 40 cents depth

        var z = LoadSingleZone(arts);
        Assert.NotNull(z.VibratoLFO);
        Assert.Equal(AbsCentsToHz(freqScale), z.VibratoLFO!.FrequencyHz, 2);
        Assert.Equal(0.5, z.VibratoLFO.DelaySeconds, 3);
        Assert.Equal(40.0, z.VibratoLFO.PitchDepthCents, 1);
    }

    [Fact]
    public void Mod_lfo_targets_pitch_volume_and_filter()
    {
        int freqScale = AbsCents(5.0);
        var arts = Art2(
            Conn(ConnectionSource.None, ConnectionDestination.LfoFrequency, freqScale),
            Conn(ConnectionSource.Lfo, ConnectionDestination.Pitch, Fixed16(25)),         // 25 cents
            Conn(ConnectionSource.Lfo, ConnectionDestination.Gain, Fixed16(60)),          // 6.0 dB (cB→dB)
            Conn(ConnectionSource.Lfo, ConnectionDestination.FilterCutoff, Fixed16(200)));

        var z = LoadSingleZone(arts);
        Assert.NotNull(z.ModulationLFO);
        Assert.Equal(AbsCentsToHz(freqScale), z.ModulationLFO!.FrequencyHz, 2);
        Assert.Equal(25.0, z.ModulationLFO.PitchDepthCents, 1);
        Assert.Equal(6.0, z.ModulationLFO.VolumeDepthDb, 2);
        Assert.Equal(200.0, z.ModulationLFO.FilterDepthCents, 1);
    }

    [Fact]
    public void External_midi_connection_becomes_route()
    {
        // KeyNumber → FilterCutoff: not part of the default-articulation set, so its presence proves a
        // bank-supplied external connection was translated into a runtime ModulationRoute.
        var arts = Art2(
            Conn(ConnectionSource.None, ConnectionDestination.FilterCutoff, AbsCents(1000)),
            Conn(ConnectionSource.KeyNumber, ConnectionDestination.FilterCutoff, Fixed16(2400)));

        var z = LoadSingleZone(arts);
        Assert.Contains(z.Routes, r =>
            r.Source is ModSource.KeyNumber && r.Dest == ModDestination.FilterCutoffCents);
    }

    [Fact]
    public void Default_articulation_routes_are_present_without_bank_articulators()
    {
        var z = LoadSingleZone(arts: null);
        // Even with no bank articulators, DLS Level 2 defaults give velocity→gain and CC7→gain etc.
        Assert.Contains(z.Routes, r => r.Source is ModSource.Velocity && r.Dest == ModDestination.AttenuationDb);
        Assert.Contains(z.Routes, r => r.Source is ModSource.ChannelController { Number: 10 } && r.Dest == ModDestination.PanNormalized);
    }

    [Fact]
    public void Drum_kit_flag_maps_to_bank_128()
    {
        var dls = MakeDls(
            Instrument(bank: 0, prog: 0, drum: true, name: "Drums",
                instArts: null,
                regions: new[] { Region(35, 81, 0, 127, 0, 0, null, null) }),
            new[] { Wave(60, 0, 0, new short[] { 1, 2, 3, 4 }, null) });

        using var bank = Load(dls);
        Assert.NotNull(bank.FindPatch(128, 0));   // drum-kit flag forces bank 128
        Assert.Null(bank.FindPatch(0, 0));
    }

    [Fact]
    public void Forward_loop_and_tuning_from_wsmp()
    {
        var wsmp = new WsmpSpec(unity: 48, fineCents: 25, gainCb1616: 0, loop: (type: 0u, start: 10u, length: 50u));
        var dls = MakeDls(
            Instrument(0, 0, false, "Looped",
                instArts: null,
                regions: new[] { Region(0, 127, 0, 127, 0, 0, null, null) }),
            new[] { WaveWithWsmp(new short[100], wsmp) });

        using var bank = Load(dls);
        var z = bank.FindPatch(0, 0)!.Zones[0];
        Assert.Equal(LoopMode.Continuous, z.Sample.LoopMode);
        Assert.Equal(48, z.Sample.OverridingRootKey);
        Assert.Equal(25.0, z.Sample.FineTuneCents, 1);
    }

    [Fact]
    public void Extensible_float_wave_decodes_as_float_not_int()
    {
        var pcm = new[] { 0.75f, 0.123f, 0.75f, 0.123f };
        var dls = MakeDls(
            Instrument(0, 0, false, "FloatExt", null,
                new[] { Region(0, 127, 0, 127, 0, 0, null, null) }),
            new[] { WaveFloatExtensible(pcm) });

        using var bank = Load(dls);
        int id = bank.FindPatch(0, 0)!.Zones[0].Sample.SampleId;
        var buf = new float[pcm.Length];
        int n = bank.Samples.ReadFrames(id, 0, buf);

        Assert.Equal(pcm.Length, n);
        // Decoded as float → matches the source. Misread as integer PCM would map every value to ~0.49
        // (the float bit-patterns interpreted as int32), so the tolerance below would fail.
        Assert.True(Math.Abs(buf[0] - 0.75f) < 1e-4, $"frame 0: {buf[0]}");
        Assert.True(Math.Abs(buf[1] - 0.123f) < 1e-4, $"frame 1: {buf[1]}");
    }

    [Fact]
    public void Wsmp_gain_adds_attenuation()
    {
        // wsmp lGain is 16.16 cB of gain (negative = quieter); the reader negates → positive attenuation.
        var wsmp = new WsmpSpec(unity: 60, fineCents: 0, gainCb1616: -Fixed16(60), loop: null);  // 60 cB = 6 dB down
        var dls = MakeDls(
            Instrument(0, 0, false, "Quiet", null,
                new[] { Region(0, 127, 0, 127, 0, 0, null, null) }),
            new[] { WaveWithWsmp(new short[16], wsmp) });

        using var bank = Load(dls);
        var z = bank.FindPatch(0, 0)!.Zones[0];
        Assert.Equal(6.0, z.Level.AttenuationDb, 2);
    }

    // ── DLS test-file builder (RIFF "DLS ") ─────────────────────────────

    private IRBank Load(byte[] dls)
    {
        string path = Path.Combine(_dir, "bank.dls");
        File.WriteAllBytes(path, dls);
        return SoundBankLoader.Load(path);
    }

    private PatchZone LoadSingleZone(byte[]? arts)
    {
        var dls = MakeDls(
            Instrument(0, 0, false, "Inst",
                instArts: arts,
                regions: new[] { Region(0, 127, 0, 127, 0, 0, null, null) }),
            new[] { Wave(60, 0, 0, new short[] { 0, 1, 2, 3, 4, 5, 6, 7 }, null) });
        return Load(dls).FindPatch(0, 0)!.Zones[0];
    }

    private readonly record struct WsmpSpec(byte unity, short fineCents, int gainCb1616, (uint type, uint start, uint length)? loop);

    private static byte[] MakeDls(byte[] instrument, byte[][] waves) => Riff("DLS ",
        Chunk("colh", U32(1)),
        List("lins", instrument),
        List("wvpl", waves));

    private static byte[] Instrument(uint bank, uint prog, bool drum, string name, byte[]? instArts, byte[][] regions)
    {
        uint localeBank = drum ? (bank | 0x80000000u) : bank;
        var parts = new List<byte[]>
        {
            Chunk("insh", Cat(U32((uint)regions.Length), U32(localeBank), U32(prog))),
            List("INFO", Chunk("INAM", Zstr(name))),
        };
        if (instArts != null) parts.Add(instArts);
        parts.Add(List("lrgn", regions));
        return List("ins ", parts.ToArray());
    }

    private static byte[] Region(byte kLo, byte kHi, byte vLo, byte vHi, ushort keyGroup, uint tableIndex,
        byte[]? arts, WsmpSpec? rgnWsmp)
    {
        var parts = new List<byte[]>
        {
            Chunk("rgnh", Cat(U16(kLo), U16(kHi), U16(vLo), U16(vHi), U16(0), U16(keyGroup))),
            Chunk("wlnk", Cat(U16(0), U16(0), U32(0), U32(tableIndex))),
        };
        if (rgnWsmp is { } w) parts.Add(Wsmp(w));
        if (arts != null) parts.Add(arts);
        return List("rgn ", parts.ToArray());
    }

    private static byte[] Wave(byte unity, short fine, int gainCb1616, short[] pcm, (uint type, uint start, uint length)? loop)
        => WaveWithWsmp(pcm, new WsmpSpec(unity, fine, gainCb1616, loop));

    private static byte[] WaveWithWsmp(short[] pcm, WsmpSpec wsmp)
    {
        var data = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, data, 0, data.Length);
        return List("wave",
            Chunk("fmt ", Cat(U16(1), U16(1), U32(44100), U32(88200), U16(2), U16(16))),  // PCM mono 16-bit
            Wsmp(wsmp),
            Chunk("data", data),
            List("INFO", Chunk("INAM", Zstr("wav"))));
    }

    /// <summary>A mono WAVE_FORMAT_EXTENSIBLE wave whose SubFormat GUID is KSDATAFORMAT_SUBTYPE_IEEE_FLOAT,
    /// carrying float32 PCM. The reader must resolve the subformat and decode it as float.</summary>
    private static byte[] WaveFloatExtensible(float[] pcm)
    {
        var data = new byte[pcm.Length * 4];
        Buffer.BlockCopy(pcm, 0, data, 0, data.Length);
        // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = {00000003-0000-0010-8000-00AA00389B71}; leading word = 0x0003.
        byte[] floatSubformat = { 0x03, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 };
        var fmt = Cat(
            U16(0xFFFE), U16(1), U32(44100), U32(44100 * 4), U16(4), U16(32),  // extensible, mono, 32-bit
            U16(22), U16(32), U32(0),    // cbSize, wValidBitsPerSample, dwChannelMask
            floatSubformat);
        return List("wave",
            Chunk("fmt ", fmt),
            Wsmp(new WsmpSpec(60, 0, 0, null)),
            Chunk("data", data),
            List("INFO", Chunk("INAM", Zstr("wav"))));
    }

    private static byte[] Wsmp(WsmpSpec w)
    {
        var head = Cat(U32(20), U16(w.unity), S16(w.fineCents), S32(w.gainCb1616), U32(0),
            U32(w.loop is null ? 0u : 1u));
        if (w.loop is { } l)
            head = Cat(head, Cat(U32(16), U32(l.type), U32(l.start), U32(l.length)));
        return Chunk("wsmp", head);
    }

    private static byte[] Art2(params byte[][] conns) =>
        List("lar2", Chunk("art2", Cat(U32(8), U32((uint)conns.Length), Cat(conns))));

    private static byte[] Conn(ConnectionSource src, ConnectionDestination dst, int scale) =>
        Cat(U16((ushort)src), U16(0), U16((ushort)dst), U16(0), S32(scale));

    // ── RIFF primitives ─────────────────────────────────────────────────

    private static byte[] Riff(string form, params byte[][] children)
    {
        var body = Cat(children);
        return Cat(Tag("RIFF"), U32((uint)(4 + body.Length)), Tag(form), body);
    }

    private static byte[] List(string type, params byte[][] children) =>
        Chunk("LIST", Cat(Tag(type), Cat(children)));

    private static byte[] Chunk(string tag, byte[] body)
    {
        var pad = (body.Length & 1) == 1 ? new byte[] { 0 } : Array.Empty<byte>();
        return Cat(Tag(tag), U32((uint)body.Length), body, pad);
    }

    private static byte[] Tag(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] U32(uint v) => BitConverter.GetBytes(v);
    private static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);
    private static byte[] S16(short v) => BitConverter.GetBytes(v);
    private static byte[] S32(int v) => BitConverter.GetBytes(v);
    private static byte[] Zstr(string s) => Cat(Encoding.ASCII.GetBytes(s), new byte[] { 0 });
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Sustain level (0..1 linear) → DLS cB-of-attenuation value (the loader inverts back).</summary>
    private static double SustainCb(double linear) => linear >= 1.0 ? 0.0 : -200.0 * Math.Log10(linear);
}
