using System;

namespace MidiSharp.Synth;

/// <summary>
/// A per-instrument insert-effect hook (Tier-2 mixing). When an instrument has one registered, the
/// synth sums that instrument's voices into a private stereo bus and hands the bus to
/// <see cref="Process"/> before mixing it to master — so the host can shape one instrument's signal
/// (EQ, limiter, …) independently of the rest.
/// </summary>
/// <remarks>
/// Deliberately a tiny interface the synth owns (interleaved stereo, mutated in place) so
/// <c>MidiSharp.Synth</c> stays decoupled from <c>MidiSharp.Dsp</c>: the host adapts a Dsp
/// <c>ProcessorChain</c> to it. Only instruments with a registered insert pay for a private bus and
/// the extra summation pass; every other instrument sums straight to master exactly as before, so
/// playback with no inserts is bit-identical to the pre-Tier-2 engine.
/// </remarks>
public interface IInstrumentInsert
{
    /// <summary>Processes one interleaved-stereo block (length = frames × 2) in place.</summary>
    void Process(Span<float> interleavedStereo);
}
