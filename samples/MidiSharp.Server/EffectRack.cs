using MidiSharp.Dsp;
using MidiSharp.Synth;

namespace MidiSharp.Server;

/// <summary>
/// A configurable effect rack: persistent EQ + limiter instances behind a <see cref="ProcessorChain"/>,
/// (re)built in order from an <see cref="EffectDto"/> list. Only enabled effects enter the chain, and
/// the instances persist across reconfigures so DSP state survives a reorder. Implements
/// <see cref="IInstrumentInsert"/> so the same class serves both the master bus and a per-instrument
/// insert — the host bridge between the Dsp processors and the synth's insert hook.
/// </summary>
internal sealed class EffectRack : IInstrumentInsert
{
    private readonly ParametricEq _eq;
    private readonly LimiterProcessor _limiter;
    private readonly GainProcessor _trailingGain = new();   // final fader (master output level); 0 dB = bypass
    private readonly ProcessorChain _chain = new();

    public EffectRack(int sampleRate)
    {
        _eq = new ParametricEq(sampleRate);
        _limiter = new LimiterProcessor(sampleRate) { Enabled = false };
    }

    /// <summary>True when no enabled effect is in the chain (so a per-instrument bus isn't worth paying for).</summary>
    public bool IsEmpty => _chain.Processors.Count == 0;

    /// <summary>Rebuilds the chain from the effect list, in order, then a final output-gain fader
    /// (<paramref name="trailingGainDb"/>, master only — appended last; 0 dB is omitted). Disabled
    /// effects keep their UI slot but stay out of the signal path.</summary>
    public void Configure(IReadOnlyList<EffectDto>? effects, double trailingGainDb = 0)
    {
        var ordered = new List<IAudioProcessor>();
        if (effects != null)
            foreach (var e in effects)
            {
                if (!e.Enabled) continue;
                switch (e.Type?.ToLowerInvariant())
                {
                    case "eq":
                        _eq.SetBands(e.EqBands is { Length: > 0 } b
                            ? Array.ConvertAll(b, ToEqSpec)
                            : []);
                        ordered.Add(_eq);
                        break;
                    case "limiter":
                        _limiter.Enabled = true;
                        _limiter.CeilingDb = e.CeilingDb;
                        _limiter.ReleaseMs = e.ReleaseMs > 0 ? e.ReleaseMs : 100.0;
                        ordered.Add(_limiter);
                        break;
                }
            }
        if (trailingGainDb != 0)   // the master fader sits last, after the inserts
        {
            _trailingGain.GainDb = trailingGainDb;
            ordered.Add(_trailingGain);
        }
        _chain.SetAll(ordered);
    }

    public void Process(Span<float> interleavedStereo) => _chain.Process(interleavedStereo);

    /// <summary>Clears the chain's filter/limiter state (e.g. on transport stop).</summary>
    public void Reset() => _chain.Reset();

    private static EqBandSpec ToEqSpec(EqBandDto b)
    {
        var type = b.Type?.ToLowerInvariant() switch
        {
            "lowshelf" => BiquadType.LowShelf,
            "highshelf" => BiquadType.HighShelf,
            "lowpass" => BiquadType.LowPass,
            "highpass" => BiquadType.HighPass,
            "notch" => BiquadType.Notch,
            _ => BiquadType.Peaking,
        };
        return new EqBandSpec(type, b.FreqHz, b.Q > 0 ? b.Q : 0.707, b.GainDb);
    }
}
