using System.Collections.Generic;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap.Tests;

public class SoundBankComposerPartTests
{
    private static readonly IReadOnlyDictionary<(int, int), PatchRef> NoPatchOverrides
        = new Dictionary<(int, int), PatchRef>();
    private static readonly IReadOnlyDictionary<int, PatchRef> NoTrackOverrides
        = new Dictionary<int, PatchRef>();

    // Mirror of SoundBankComposer.PartKey / Synthesizer.TrackPart (channel is 4-bit).
    private static int PartKey(int track, int channel) => (track << 4) | (channel & 0xF);

    private static float FirstFrame(IRBank bank, int sampleId)
    {
        var buf = new float[4];
        bank.Samples.ReadFrames(sampleId, 0, buf);
        return buf[0];
    }

    [Fact]
    public void PartOverride_PlacedAtSyntheticAddress_AndMapped()
    {
        using var baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using var flute = TestBanks.OneSamplePatch("flute", 0.6f, 0, 73, "Flute");
        var partOverrides = new Dictionary<(int, int), PatchRef> { [(0, 5)] = new PatchRef(flute, 0, 73) };

        var result = SoundBankComposer.BuildComposite(baseBank, NoPatchOverrides, NoTrackOverrides, partOverrides);

        var key = PartKey(0, 5);
        Assert.True(result.PartPatchMap.TryGetValue(key, out var addr));
        Assert.Equal((SoundBankComposer.PartOverrideBank, key), addr);
        var patch = result.Bank.FindPatch(addr.Bank, addr.Program);
        Assert.NotNull(patch);
        Assert.Equal("Flute", patch!.Name);
        Assert.Equal(0.6f, FirstFrame(result.Bank, patch.Zones[0].Sample.SampleId), 3);
        // The base patch is untouched, so other channels still resolve normally.
        Assert.Equal("Piano", result.Bank.FindPatch(0, 0)!.Name);
    }

    [Fact]
    public void TwoChannels_SameTrack_SubstituteIndependently()
    {
        // The format-0 case: one track, two channels carrying different instruments. Each part is
        // overridden on its own and lands at a distinct synthetic address.
        using var baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using var flute = TestBanks.OneSamplePatch("flute", 0.6f, 0, 73, "Flute");
        using var bass = TestBanks.OneSamplePatch("bass", 0.4f, 0, 33, "Bass");
        var partOverrides = new Dictionary<(int, int), PatchRef>
        {
            [(0, 5)] = new PatchRef(flute, 0, 73),
            [(0, 6)] = new PatchRef(bass, 0, 33),
        };

        var result = SoundBankComposer.BuildComposite(baseBank, NoPatchOverrides, NoTrackOverrides, partOverrides);

        var ch5 = result.Bank.FindPatch(result.PartPatchMap[PartKey(0, 5)].Bank, result.PartPatchMap[PartKey(0, 5)].Program);
        var ch6 = result.Bank.FindPatch(result.PartPatchMap[PartKey(0, 6)].Bank, result.PartPatchMap[PartKey(0, 6)].Program);
        Assert.Equal("Flute", ch5!.Name);
        Assert.Equal("Bass", ch6!.Name);
        Assert.Equal(0.6f, FirstFrame(result.Bank, ch5.Zones[0].Sample.SampleId), 3);
        Assert.Equal(0.4f, FirstFrame(result.Bank, ch6.Zones[0].Sample.SampleId), 3);
    }

    [Fact]
    public void PartAndTrackOverride_DoNotCollide_OnDistinctBanks()
    {
        // A whole-track override and a per-part override for the same track produce separate synthetic
        // addresses on separate reserved banks; the synth consults the part map first.
        using var baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using var strings = TestBanks.OneSamplePatch("strings", 0.5f, 0, 48, "Strings");
        using var flute = TestBanks.OneSamplePatch("flute", 0.6f, 0, 73, "Flute");
        var trackOverrides = new Dictionary<int, PatchRef> { [0] = new PatchRef(strings, 0, 48) };
        var partOverrides = new Dictionary<(int, int), PatchRef> { [(0, 5)] = new PatchRef(flute, 0, 73) };

        var result = SoundBankComposer.BuildComposite(baseBank, NoPatchOverrides, trackOverrides, partOverrides);

        var trackAddr = result.TrackPatchMap[0];
        var partAddr = result.PartPatchMap[PartKey(0, 5)];
        Assert.Equal(SoundBankComposer.TrackOverrideBank, trackAddr.Bank);
        Assert.Equal(SoundBankComposer.PartOverrideBank, partAddr.Bank);
        Assert.Equal("Strings", result.Bank.FindPatch(trackAddr.Bank, trackAddr.Program)!.Name);
        Assert.Equal("Flute", result.Bank.FindPatch(partAddr.Bank, partAddr.Program)!.Name);
    }

    [Fact]
    public void UnresolvedPartOverride_NotMapped_LeavesChannelResolutionIntact()
    {
        using var baseBank = TestBanks.OneSamplePatch("base", 0.1f, 0, 0, "Piano");
        using var src = TestBanks.OneSamplePatch("src", 0.7f, 0, 5, "Distortion");
        // The source has no patch at (9, 9): the part override is skipped entirely.
        var partOverrides = new Dictionary<(int, int), PatchRef> { [(0, 3)] = new PatchRef(src, 9, 9) };

        var result = SoundBankComposer.BuildComposite(baseBank, NoPatchOverrides, NoTrackOverrides, partOverrides);

        Assert.False(result.PartPatchMap.ContainsKey(PartKey(0, 3)));
        Assert.Null(result.Bank.FindPatch(SoundBankComposer.PartOverrideBank, PartKey(0, 3)));
        Assert.Equal("Piano", result.Bank.FindPatch(0, 0)!.Name);
    }
}
