using System;
using System.Collections.Generic;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// A managed in-memory <see cref="IHostedPlugin"/> standing in for a native effect: it multiplies each
/// planar channel by a single normalized "Gain" parameter (0..2, default ×1). Lets the format-agnostic
/// host machinery (PlanarBridge, HostedEffect, the no-GC loop, the ProcessorChain drop-in) be verified
/// with exact arithmetic and no native plugin present.
/// </summary>
internal sealed class FakeGainPlugin : IHostedPlugin
{
    private readonly PluginParameter _gain = new(0, "Gain", "", 0.0, 2.0, 1.0);
    private double _normalized = 0.5;   // ×1.0 (normalized midpoint of 0..2)

    public PluginDescriptor Descriptor { get; } =
        new("FAKE", "1", "Fake Gain", "Test", IsInstrument: false, Path: "");
    public bool IsInstrument => false;
    public IReadOnlyList<PluginParameter> Parameters => [_gain];

    public void Activate(AudioConfig config) { }
    public void Deactivate() { }

    public void Process(PlanarBuffers input, PlanarBuffers output, ReadOnlySpan<HostEvent> events)
    {
        var g = (float)_gain.Denormalize(_normalized);
        for (var c = 0; c < output.ChannelCount; c++)
        {
            Span<float> src = input.ChannelSpan(c);
            Span<float> dst = output.ChannelSpan(c);
            for (var i = 0; i < dst.Length; i++) dst[i] = src[i] * g;
        }
    }

    public double GetParameter(int index) => _normalized;
    public void SetParameter(int index, double normalized) => _normalized = normalized;
    public byte[] SaveState() => [];
    public void LoadState(ReadOnlySpan<byte> state) { }
    public void Dispose() { }
}
