using System;

namespace MidiSharp.Dsp;

/// <summary>
/// A trivial master gain/trim processor: scales the whole interleaved-stereo block by a dB value.
/// 0 dB is a pass-through (skipped without touching the buffer). Stateless — <see cref="Reset"/> is a
/// no-op. Useful as a master output trim and as the simplest example of the <see cref="IAudioProcessor"/>
/// contract.
/// </summary>
public sealed class GainProcessor : IAudioProcessor
{
    /// <summary>Gain in dB (positive = louder). Default 0 (unity).</summary>
    public double GainDb { get; set; }

    public void Process(Span<float> interleavedStereo)
    {
        if (GainDb == 0.0) return;
        var g = (float)Math.Pow(10.0, GainDb / 20.0);
        for (var i = 0; i < interleavedStereo.Length; i++)
            interleavedStereo[i] *= g;
    }

    public void Reset() { }
}
