namespace MidiSharp.Audio;

/// <summary>
/// Sample-file metadata read from a format header without decoding the audio payload —
/// the cheap counterpart to <see cref="DecodedAudio"/> for lazy/streaming sample sources.
/// </summary>
/// <remarks>
/// <see cref="Channels"/>/<see cref="SampleRate"/>/<see cref="FrameCount"/> come straight from the
/// header. <see cref="RootKey"/> and the loop fields are best-effort: a header peek may not reach
/// them (e.g. a WAV <c>smpl</c> chunk after the audio data), in which case they default to "none".
/// For SFZ that's fine — its opcodes supply root key and loop. A <see cref="FrameCount"/> of 0 means
/// the supplied bytes were too short to determine length (the caller should retry with the full file).
/// </remarks>
public readonly struct AudioInfo
{
    public int Channels { get; init; }
    public int SampleRate { get; init; }
    public long FrameCount { get; init; }
    public int RootKey { get; init; }
    public double FineTuneCents { get; init; }
    public long LoopStartFrame { get; init; }
    public long LoopEndFrame { get; init; }

    public bool HasLoop => LoopStartFrame >= 0 && LoopEndFrame > LoopStartFrame;

    public static AudioInfo None => new()
    {
        Channels = 1,
        SampleRate = 44100,
        FrameCount = 0,
        RootKey = -1,
        LoopStartFrame = -1,
        LoopEndFrame = -1,
    };
}
