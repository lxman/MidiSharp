using System;

namespace MidiSharp.Audio;

/// <summary>
/// The result of decoding an audio file: normalized float32 samples plus the
/// metadata a sampler needs. This is the single currency every decoder produces
/// and every loader consumes — samples are always interleaved float in the
/// range [-1, 1], regardless of the source format or its native bit depth.
/// </summary>
public sealed class DecodedAudio
{
    /// <summary>Interleaved float32 samples in [-1, 1] (length = FrameCount × Channels).</summary>
    public float[] Samples { get; init; } = [];

    public int Channels { get; init; } = 1;

    public int SampleRate { get; init; } = 44100;

    /// <summary>The source file's native bit depth (for diagnostics; samples are always float).</summary>
    public int BitsPerSample { get; init; }

    public long FrameCount { get; init; }

    /// <summary>MIDI unity/root note from the container (WAV smpl, AIFF INST), or -1 if none.</summary>
    public int RootKey { get; init; } = -1;

    /// <summary>Fine-tune cents from the container, 0 if none.</summary>
    public double FineTuneCents { get; init; }

    /// <summary>First loop's start frame (sample-relative), or -1 if no loop.</summary>
    public long LoopStartFrame { get; init; } = -1;

    /// <summary>First loop's end frame (sample-relative, exclusive), or -1 if no loop.</summary>
    public long LoopEndFrame { get; init; } = -1;

    public bool HasLoop => LoopStartFrame >= 0 && LoopEndFrame > LoopStartFrame;
}
