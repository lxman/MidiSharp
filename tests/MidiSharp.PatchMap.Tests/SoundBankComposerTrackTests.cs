using System.Collections.Generic;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap.Tests;

public class SoundBankComposerTrackTests
{
    private static readonly IReadOnlyDictionary<(int, int), PatchRef> NoPatchOverrides
        = new Dictionary<(int, int), PatchRef>();

    private static float FirstFrame(IRBank bank, int sampleId)
    {
        var buf = new float[4];
        bank.Samples.ReadFrames(sampleId, 0, buf);
        return buf[0];
    }

    [Fact]
    public void TrackOverride_PlacedAtSyntheticAddress_AndMapped()
    {
        using IRBank baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using IRBank cello = TestBanks.OneSamplePatch("cello", 0.7f, 0, 42, "Cello");
        var trackOverrides = new Dictionary<int, PatchRef> { [11] = new PatchRef(cello, 0, 42) };

        CompositeResult result = SoundBankComposer.BuildComposite(baseBank, NoPatchOverrides, trackOverrides);

        // Track 11 is mapped to the reserved synthetic address...
        Assert.True(result.TrackPatchMap.TryGetValue(11, out (int Bank, int Program) addr));
        Assert.Equal((SoundBankComposer.TrackOverrideBank, 11), addr);
        // ...and a patch sourced from the cello font lives there, sounding the cello sample.
        Patch? patch = result.Bank.FindPatch(addr.Bank, addr.Program);
        Assert.NotNull(patch);
        Assert.Equal("Cello", patch!.Name);
        Assert.Equal(0.7f, FirstFrame(result.Bank, patch.Zones[0].Sample.SampleId), 3);
        // The base patch is untouched, so an unmapped track still resolves normally.
        Assert.Equal("Piano", result.Bank.FindPatch(0, 0)!.Name);
    }

    [Fact]
    public void TwoTracks_SameSourcePatch_GetDistinctSyntheticAddresses()
    {
        // Viola (track 10) and Cello (track 11) collide on the same channel/program in the file,
        // but as separate tracks they must route independently.
        using IRBank baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using IRBank viola = TestBanks.OneSamplePatch("viola", 0.3f, 0, 41, "Viola");
        using IRBank cello = TestBanks.OneSamplePatch("cello", 0.7f, 0, 42, "Cello");
        var trackOverrides = new Dictionary<int, PatchRef>
        {
            [10] = new PatchRef(viola, 0, 41),
            [11] = new PatchRef(cello, 0, 42),
        };

        CompositeResult result = SoundBankComposer.BuildComposite(baseBank, NoPatchOverrides, trackOverrides);

        Patch? violaPatch = result.Bank.FindPatch(result.TrackPatchMap[10].Bank, result.TrackPatchMap[10].Program);
        Patch? celloPatch = result.Bank.FindPatch(result.TrackPatchMap[11].Bank, result.TrackPatchMap[11].Program);
        Assert.Equal("Viola", violaPatch!.Name);
        Assert.Equal("Cello", celloPatch!.Name);
        Assert.Equal(0.3f, FirstFrame(result.Bank, violaPatch.Zones[0].Sample.SampleId), 3);
        Assert.Equal(0.7f, FirstFrame(result.Bank, celloPatch.Zones[0].Sample.SampleId), 3);
    }

    [Fact]
    public void UnresolvedTrackOverride_NotMapped_LeavesChannelResolutionIntact()
    {
        using IRBank baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using IRBank src = TestBanks.OneSamplePatch("src", 0.7f, 0, 5, "Distortion");
        // The source has no patch at (9, 9): the track override is skipped entirely.
        var trackOverrides = new Dictionary<int, PatchRef> { [3] = new PatchRef(src, 9, 9) };

        CompositeResult result = SoundBankComposer.BuildComposite(baseBank, NoPatchOverrides, trackOverrides);

        Assert.False(result.TrackPatchMap.ContainsKey(3));
        Assert.Null(result.Bank.FindPatch(SoundBankComposer.TrackOverrideBank, 3));
        Assert.Equal("Piano", result.Bank.FindPatch(0, 0)!.Name);
    }

    [Fact]
    public void PatchAndTrackOverrides_Coexist_SharingOneSourceFontOffset()
    {
        using IRBank baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using IRBank src = TestBanks.OneSamplePatch("src", 0.7f, 0, 5, "Distortion");
        var patchOverrides = new Dictionary<(int, int), PatchRef> { [(0, 0)] = new PatchRef(src, 0, 5) };
        var trackOverrides = new Dictionary<int, PatchRef> { [11] = new PatchRef(src, 0, 5) };

        CompositeResult result = SoundBankComposer.BuildComposite(baseBank, patchOverrides, trackOverrides);

        // One source font → one extra sample slot shared by both overrides (base 1 + src 1 = 2).
        Assert.Equal(2, result.Bank.Samples.Count);
        Assert.Equal("Distortion", result.Bank.FindPatch(0, 0)!.Name);
        Patch? track = result.Bank.FindPatch(result.TrackPatchMap[11].Bank, result.TrackPatchMap[11].Program);
        Assert.Equal("Distortion", track!.Name);
        Assert.Equal(0.7f, FirstFrame(result.Bank, track.Zones[0].Sample.SampleId), 3);
    }
}
