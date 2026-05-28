using System;

namespace MidiSharp.Synth;

/// <summary>
/// Delegate for audio buffer fill callbacks.
/// </summary>
/// <param name="buffer">Interleaved stereo float buffer to fill</param>
/// <param name="frames">Number of frames (buffer.Length / 2 for stereo)</param>
public delegate void AudioCallback(float[] buffer, int frames);

/// <summary>
/// Interface for platform-specific audio output implementations.
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>
    /// Gets the sample rate of the audio output.
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    /// Gets the number of channels (typically 2 for stereo).
    /// </summary>
    int Channels { get; }

    /// <summary>
    /// Gets whether the audio output is currently playing.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Starts audio playback.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops audio playback.
    /// </summary>
    void Stop();

    /// <summary>
    /// Sets the callback that will be invoked to fill audio buffers.
    /// </summary>
    /// <param name="callback">
    /// Callback that receives an interleaved float buffer to fill.
    /// The buffer contains stereo samples (left, right, left, right, ...).
    /// </param>
    void SetCallback(AudioCallback callback);
}

/// <summary>
/// Extension methods for IAudioOutput.
/// </summary>
public static class AudioOutputExtensions
{
    /// <summary>
    /// Creates a callback that generates audio from a Synthesizer.
    /// </summary>
    public static void SetSynthesizer(this IAudioOutput output, Synthesizer synth)
    {
        output.SetCallback((buffer, frames) => synth.GenerateInterleaved(buffer.AsSpan(0, frames * 2)));
    }
}
