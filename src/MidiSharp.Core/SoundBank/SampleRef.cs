namespace MidiSharp.SoundBank;

/// <summary>
/// Looping behavior for a zone's sample playback.
/// </summary>
public enum LoopMode
{
    /// <summary>One-shot: play once and stop.</summary>
    None,

    /// <summary>Loop indefinitely while held; continue looping during release.</summary>
    Continuous,

    /// <summary>Loop while held; stop looping at NoteOff (release plays to end).</summary>
    UntilRelease,
}

/// <summary>
/// Per-zone sample reference: which sample to play, how to address it, and how
/// to tune it. All frame-offset fields are sample-relative (frame 0 = first
/// frame of this sample), not chunk-relative.
/// </summary>
public sealed class SampleRef
{
    /// <summary>Index into <see cref="SoundBank.Samples"/>.</summary>
    public int SampleId { get; init; }

    public LoopMode LoopMode { get; init; }

    /// <summary>Override the sample's metadata root key. Null = use sample's own.</summary>
    public int? OverridingRootKey { get; init; }

    /// <summary>Per-sample fine tune in cents (combines with <see cref="PitchSettings.FineTuneCents"/>).</summary>
    public double FineTuneCents { get; init; }

    /// <summary>Per-sample coarse tune in semitones (combines with <see cref="PitchSettings.CoarseTuneSemitones"/>).</summary>
    public double CoarseTuneSemitones { get; init; }

    /// <summary>
    /// Cents per key applied to the pitch difference between the played key
    /// and the root key. Default 100 = equal temperament. 0 = no key tracking
    /// (all keys play at the sample's recorded pitch).
    /// </summary>
    public double ScaleTuningCentsPerKey { get; init; } = 100.0;

    /// <summary>Sample-relative start frame override. Null = use sample's metadata.</summary>
    public long? StartOffset { get; init; }

    /// <summary>Sample-relative end frame override. Null = use sample's metadata.</summary>
    public long? EndOffset { get; init; }

    /// <summary>Sample-relative loop-start override. Null = use sample's metadata.</summary>
    public long? LoopStartOffset { get; init; }

    /// <summary>Sample-relative loop-end override. Null = use sample's metadata.</summary>
    public long? LoopEndOffset { get; init; }
}
