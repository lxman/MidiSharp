namespace MidiSharp.SoundBank;

/// <summary>
/// The destination a v2 generic LFO (SFZ <c>lfoN_*</c>) modulates. Distinct from the SF2 two-slot
/// LFO model (<see cref="LFOSettings"/>): an SFZ <c>lfoN</c> is an indexed oscillator that can be
/// routed to any of these targets, each with its own depth.
/// </summary>
public enum LfoDestination
{
    Pitch,      // cents
    Volume,     // dB
    Cutoff,     // cents (filter)
    Pan,        // -1..1 normalized
    Amplitude,  // linear gain fraction
    Width,      // stereo width fraction
    EqGain,     // dB on an EQ band
    EqFreq,     // cents/Hz on an EQ band
}

/// <summary>A CC that scales a value (an LFO frequency or a target depth) by <see cref="Amount"/> at full CC.</summary>
public readonly struct LfoCcDepth
{
    public LfoCcDepth(int cc, double amount)
    {
        Cc = cc;
        Amount = amount;
    }

    public int Cc { get; }
    public double Amount { get; }
}

/// <summary>
/// One additive stage of a complex LFO (SFZ <c>lfoN_waveX/ratioX/scaleX/offsetX</c>). Stage 0 is the
/// main waveform (ratio 1, scale 1, offset 0); stages 1..6 are sub-waveforms summed onto it, each at a
/// frequency multiple (<see cref="Ratio"/>), amplitude (<see cref="Scale"/>) and DC <see cref="Offset"/>.
/// </summary>
public readonly struct LfoStage
{
    public LfoStage(int wave, double ratio, double scale, double offset)
    {
        Wave = wave;
        Ratio = ratio;
        Scale = scale;
        Offset = offset;
    }

    /// <summary>Waveform number (0=triangle, 1=sine, 2..5=pulse widths, 6=saw up, 7=saw down, 12=S&amp;H, 13=stepped).</summary>
    public int Wave { get; }
    public double Ratio { get; }
    public double Scale { get; }
    public double Offset { get; }
}

/// <summary>One destination an LFO drives, with a depth that may be scaled by one or more CCs.</summary>
public sealed class LfoTarget
{
    public LfoDestination Destination { get; init; }

    /// <summary>Base modulation depth in the destination's units (cents, dB, …).</summary>
    public double Depth { get; init; }

    /// <summary>1-based EQ band index for <see cref="LfoDestination.EqGain"/> / <see cref="LfoDestination.EqFreq"/>.</summary>
    public int EqBand { get; init; }

    /// <summary>CCs that scale <see cref="Depth"/> — the mod-wheel-vibrato mechanism (lfoN_{target}_onccX). Null = none.</summary>
    public LfoCcDepth[]? DepthCc { get; init; }
}

/// <summary>
/// An SFZ v2 generic LFO (<c>lfoN</c>): an indexed oscillator (one or more summed wave stages, with
/// delay, fade-in and initial phase, and an optionally CC-modulated frequency) routed to one or more
/// <see cref="LfoTarget"/>s. Runs per-sample in the synth voice, alongside — not replacing — the SF2
/// two-slot LFOs. Present only on SFZ zones that declare <c>lfoN_*</c> opcodes.
/// </summary>
public sealed class GenericLfo
{
    public double FrequencyHz { get; init; }
    public double DelaySeconds { get; init; }
    public double FadeSeconds { get; init; }

    /// <summary>Initial phase, 0..1 of a cycle.</summary>
    public double Phase { get; init; }

    /// <summary>Wave stages; index 0 is the main waveform. Always at least one stage.</summary>
    public LfoStage[] Stages { get; init; } = [];

    /// <summary>CCs that modulate the LFO frequency in Hz (lfoN_freq_onccX). Null = none.</summary>
    public LfoCcDepth[]? FreqCc { get; init; }

    public LfoTarget[] Targets { get; init; } = [];
}
