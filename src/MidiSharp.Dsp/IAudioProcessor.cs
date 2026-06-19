using System;

namespace MidiSharp.Dsp;

/// <summary>
/// A buffer-in / buffer-out DSP processor — a "plugin". It mutates one interleaved-stereo float block
/// in place: the block holds <c>frames × 2</c> samples where index <c>2i</c> is the left sample and
/// <c>2i+1</c> the right.
/// </summary>
/// <remarks>
/// A processor knows nothing about MIDI, voices, or how the block was produced. The host inserts
/// processors into the audio-callback seam — after the synth fills the block and before it reaches
/// the device — so the synth never depends on this library. The same contract is reused later for
/// per-instrument insert effects, where the block is one instrument's bus buffer rather than the
/// master stereo mix.
/// </remarks>
public interface IAudioProcessor
{
    /// <summary>
    /// Processes one interleaved-stereo block in place. <paramref name="interleavedStereo"/> length is
    /// <c>frames × 2</c>; an odd-length span has its final lone sample left untouched.
    /// </summary>
    void Process(Span<float> interleavedStereo);

    /// <summary>
    /// Clears internal filter/delay state (e.g. on transport seek, stop, or a hard parameter jump) so
    /// the next block starts clean. Does not change configured parameters.
    /// </summary>
    void Reset();
}
