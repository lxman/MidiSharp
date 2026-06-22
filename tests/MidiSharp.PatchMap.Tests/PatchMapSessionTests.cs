using System;
using MidiSharp.SoundBank;
using Xunit;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap.Tests;

public class PatchMapSessionTests
{
    // Records whether it was disposed, so we can prove the session controls font lifetime.
    private sealed class FlagSource : ISampleSource
    {
        public bool Disposed;
        public int Count => 0;
        public SampleMetadata Metadata(int sampleId) => new();
        public int ReadFrames(int sampleId, long frameOffset, Span<float> dest) => 0;
        public void PrepareSample(int sampleId) { }
        public void Dispose() => Disposed = true;
    }

    private static IRBank BankWith(FlagSource src, string name)
        => new() { Name = name, Patches = [], Samples = src };

    [Fact]
    public void Dispose_DisposesBaseAndAllSources()
    {
        var baseSrc = new FlagSource();
        var fontSrc = new FlagSource();
        var session = new PatchMapSession(BankWith(baseSrc, "base"));
        session.AddSource(BankWith(fontSrc, "font"));

        session.Dispose();

        Assert.True(baseSrc.Disposed);
        Assert.True(fontSrc.Disposed);
    }

    [Fact]
    public void BuildComposite_IsBorrowed_DoesNotDisposeFonts()
    {
        var baseSrc = new FlagSource();
        var session = new PatchMapSession(BankWith(baseSrc, "base"));

        IRBank composite = session.BuildComposite();
        composite.Dispose();                 // a borrowed view — must not kill the font...

        Assert.False(baseSrc.Disposed);      // ...so it survives for the next playback
        session.Dispose();
        Assert.True(baseSrc.Disposed);       // the session owns and disposes it
    }
}
