using System.Collections.Generic;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap.Tests;

public class SoundBankComposerTests
{
    private static readonly IReadOnlyDictionary<(int, int), PatchRef> NoOverrides
        = new Dictionary<(int, int), PatchRef>();

    private static float FirstFrame(IRBank bank, int sampleId)
    {
        var buf = new float[4];
        int n = bank.Samples.ReadFrames(sampleId, 0, buf);
        Assert.Equal(4, n);
        return buf[0];
    }

    [Fact]
    public void Composite_PreservesBaseInitialControllers()
    {
        // SFZ <control> set_ccN seeds (e.g. SSO/VPO seed CC1≈96 for mod-wheel dynamics) must survive
        // compositing — the web player always composites, even with no overrides.
        var data = new[] { new[] { 0.5f, 0.5f, 0.5f, 0.5f } };
        var meta = new[] { new SampleMetadata { SampleRate = 44100, Channels = 1, LengthFrames = 4, RootKey = 60 } };
        var baseBank = new IRBank
        {
            Name = "sso",
            Patches =
            [
                new Patch { Bank = 0, Program = 0, Name = "x",
                Zones = [new PatchZone { Sample = new SampleRef { SampleId = 0 } }]
                }
            ],
            Samples = new PreDecodedFloatSampleSource(data, meta),
            InitialControllers = new Dictionary<int, int> { { 1, 96 } },
        };

        IRBank composite = SoundBankComposer.BuildComposite(baseBank, NoOverrides);
        Assert.Equal(96, composite.InitialControllers[1]);
    }

    [Fact]
    public void NoOverrides_PreservesBasePatchesAndSamples()
    {
        using IRBank baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");

        IRBank comp = SoundBankComposer.BuildComposite(baseBank, NoOverrides);

        Patch? p = comp.FindPatch(0, 0);
        Assert.NotNull(p);
        Assert.Equal("Piano", p!.Name);
        Assert.Equal(baseBank.Samples.Count, comp.Samples.Count);
        Assert.Equal(0.1f, FirstFrame(comp, p.Zones[0].Sample.SampleId), 3);
    }

    [Fact]
    public void Override_PullsSoundFromSourceFont_WithRebasedSampleId()
    {
        using IRBank baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using IRBank srcBank = TestBanks.OneSamplePatch("src", 0.7f, 0, 5, "Distortion");
        var overrides = new Dictionary<(int, int), PatchRef> { [(0, 0)] = new PatchRef(srcBank, 0, 5) };

        IRBank comp = SoundBankComposer.BuildComposite(baseBank, overrides);

        Patch? p = comp.FindPatch(0, 0);
        Assert.NotNull(p);
        Assert.Equal("Distortion", p!.Name);
        // base has 1 sample (id 0); the borrowed source sample is rebased to id 1.
        Assert.Equal(1, p.Zones[0].Sample.SampleId);
        Assert.Equal(2, comp.Samples.Count);
        Assert.Equal(0.7f, FirstFrame(comp, p.Zones[0].Sample.SampleId), 3);
        Assert.Equal(0.1f, FirstFrame(comp, 0), 3);   // base sample still intact at id 0
    }

    [Fact]
    public void OverrideToNewAddress_LeavesBaseAddressIntact()
    {
        using IRBank baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using IRBank srcBank = TestBanks.OneSamplePatch("src", 0.7f, 0, 5, "Distortion");
        var overrides = new Dictionary<(int, int), PatchRef> { [(0, 30)] = new PatchRef(srcBank, 0, 5) };

        IRBank comp = SoundBankComposer.BuildComposite(baseBank, overrides);

        Assert.Equal("Piano", comp.FindPatch(0, 0)!.Name);
        Patch? moved = comp.FindPatch(0, 30);
        Assert.NotNull(moved);
        Assert.Equal("Distortion", moved!.Name);
        Assert.Equal(0.7f, FirstFrame(comp, moved.Zones[0].Sample.SampleId), 3);
    }

    [Fact]
    public void UnresolvedOverride_FallsBackToBase()
    {
        using IRBank baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using IRBank srcBank = TestBanks.OneSamplePatch("src", 0.7f, 0, 5, "Distortion");
        // The source font has no patch at (9, 9): the override is skipped, base stays.
        var overrides = new Dictionary<(int, int), PatchRef> { [(0, 0)] = new PatchRef(srcBank, 9, 9) };

        IRBank comp = SoundBankComposer.BuildComposite(baseBank, overrides);

        Assert.Equal("Piano", comp.FindPatch(0, 0)!.Name);
        Assert.Equal(0.1f, FirstFrame(comp, comp.FindPatch(0, 0)!.Zones[0].Sample.SampleId), 3);
    }

    [Fact]
    public void StereoLinkSampleId_RebasedIntoCompositeSpace()
    {
        using IRBank baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using IRBank srcBank = MakeStereoSource();
        var overrides = new Dictionary<(int, int), PatchRef> { [(0, 30)] = new PatchRef(srcBank, 0, 5) };

        IRBank comp = SoundBankComposer.BuildComposite(baseBank, overrides);

        // base has 1 sample (id 0); the source's two samples occupy ids 1 and 2. The source's
        // sample 0 (global id 1) links to its local sample 1, which must rebase to global id 2.
        Assert.Equal(3, comp.Samples.Count);
        Assert.Equal(2, comp.Samples.Metadata(1).StereoLinkSampleId);
    }

    private static IRBank MakeStereoSource()
    {
        var data = new[]
        {
            new[] { 0.7f, 0.7f, 0.7f, 0.7f },
            new[] { 0.5f, 0.5f, 0.5f, 0.5f },
        };
        var meta = new[]
        {
            new SampleMetadata { SampleRate = 44100, Channels = 1, LengthFrames = 4, RootKey = 60, StereoLinkSampleId = 1 },
            new SampleMetadata { SampleRate = 44100, Channels = 1, LengthFrames = 4, RootKey = 60, StereoLinkSampleId = 0 },
        };
        var zone = new PatchZone { Sample = new SampleRef { SampleId = 0 } };
        var patch = new Patch { Bank = 0, Program = 5, Name = "StereoSrc", Zones = [zone] };
        return new IRBank { Name = "stereo", Patches = [patch], Samples = new PreDecodedFloatSampleSource(data, meta) };
    }
}
