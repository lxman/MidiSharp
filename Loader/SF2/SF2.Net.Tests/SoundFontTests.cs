namespace SF2.Net.Tests;

public class SoundFontTests
{
    [Fact]
    public void Load_ParsesInfoChunk()
    {
        var bytes = SyntheticSoundFont.Build();
        var sf = SoundFont.Load(bytes);

        Assert.Equal("Test Bank", sf.Info.BankName);
        Assert.Equal("EMU8000", sf.Info.SoundEngine);
        Assert.Equal(new VersionTag(2, 1), sf.Info.SpecVersion);
    }

    [Fact]
    public void Load_FindsSinglePreset()
    {
        var sf = SoundFont.Load(SyntheticSoundFont.Build());

        Assert.Single(sf.Presets);
        var p = sf.Presets[0];
        Assert.Equal("TestPiano", p.Name);
        Assert.Equal(0, p.Bank);
        Assert.Equal(0, p.Number);
        Assert.Single(p.Zones);
    }

    [Fact]
    public void Load_LinksZoneToInstrument()
    {
        var sf = SoundFont.Load(SyntheticSoundFont.Build());

        var inst = sf.GetZoneInstrument(0, 0, 0);
        Assert.NotNull(inst);
        Assert.Equal("TestInst", inst!.Name);
        Assert.True(sf.HasInstrument(0, 0, 0));
    }

    [Fact]
    public void Load_LinksInstrumentZoneToSample()
    {
        var sf = SoundFont.Load(SyntheticSoundFont.Build());

        Assert.True(sf.HasSample(0, 0, 0, 0));
        var info = sf.GetSampleInfo(0, 0, 0, 0);
        Assert.NotNull(info);
        Assert.Equal("TestSample", info!.Name);
        Assert.Equal(22050u, info.SampleRate);
        Assert.Equal(1024u, info.LengthFrames);
    }

    [Fact]
    public void GetSampleData_DecodesPcm()
    {
        var sf = SoundFont.Load(SyntheticSoundFont.Build());
        var samples = sf.GetSampleData(0, 0, 0, 0);

        Assert.Equal(1024, samples.Length);
        // We synthesized a sine — should hit both positive and negative.
        Assert.Contains(samples, s => s > 1000);
        Assert.Contains(samples, s => s < -1000);
    }

    [Fact]
    public void Banks_ReturnsDistinctSortedBanks()
    {
        var sf = SoundFont.Load(SyntheticSoundFont.Build());
        Assert.Equal(new[] { 0 }, sf.Banks);
    }

    [Fact]
    public void FindPreset_ReturnsNullWhenMissing()
    {
        var sf = SoundFont.Load(SyntheticSoundFont.Build());
        Assert.Null(sf.FindPreset(99, 99));
    }

    [Fact]
    public void Load_RejectsNonRiffData()
    {
        var bad = new byte[100];
        Assert.Throws<SoundFontException>(() => SoundFont.Load(bad));
    }

    [Fact]
    public void Load_RejectsMissingFile()
    {
        var ex = Assert.Throws<SoundFontException>(() => SoundFont.Load("/tmp/__nonexistent_sf2_for_test__.sf2"));
        Assert.Contains(SoundFontValidationCode.BadFileName, ex.Codes);
    }

    [Fact]
    public void ExtractPreset_RoundTripsThroughLoad()
    {
        var original = SoundFont.Load(SyntheticSoundFont.Build());
        var extracted = original.ExtractPreset(0, 0);

        Assert.NotEmpty(extracted);

        var reloaded = SoundFont.Load(extracted);
        Assert.Single(reloaded.Presets);
        var p = reloaded.Presets[0];
        Assert.Equal("TestPiano", p.Name);
        Assert.True(reloaded.HasInstrument(0, 0, 0));
        Assert.True(reloaded.HasSample(0, 0, 0, 0));
        Assert.Equal(22050u, reloaded.GetSampleInfo(0, 0, 0, 0)!.SampleRate);
    }
}
