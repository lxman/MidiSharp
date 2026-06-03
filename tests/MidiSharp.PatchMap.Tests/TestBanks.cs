using MidiSharp.SoundBank;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap.Tests;

internal static class TestBanks
{
    /// <summary>
    /// A bank holding one mono sample (a constant value across 4 frames) addressed by one patch
    /// at (<paramref name="bank"/>, <paramref name="program"/>). The constant value lets a test
    /// identify which font a composite sample came from.
    /// </summary>
    public static IRBank OneSamplePatch(string name, float sampleValue, int bank, int program, string patchName)
    {
        var data = new[] { new[] { sampleValue, sampleValue, sampleValue, sampleValue } };
        var meta = new[] { new SampleMetadata { SampleRate = 44100, Channels = 1, LengthFrames = 4, RootKey = 60 } };
        var zone = new PatchZone { Sample = new SampleRef { SampleId = 0 } };
        var patch = new Patch { Bank = bank, Program = program, Name = patchName, Zones = new[] { zone } };
        return new IRBank
        {
            Name = name,
            Patches = new[] { patch },
            Samples = new PreDecodedFloatSampleSource(data, meta),
        };
    }
}
