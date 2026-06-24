using System;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Sources;

namespace MidiSharp.Synth.OwnAudio;

/// <summary>
/// OwnAudio source that pulls interleaved float frames from a user-supplied
/// <see cref="AudioCallback"/>. The mixer calls <see cref="ReadSamples"/>; we
/// hand off to the synth's callback and copy the result into the output span.
/// </summary>
internal sealed class SynthCallbackSource(int sampleRate, int channels) : BaseAudioSource
{
    private readonly AudioConfig _config = new()
    {
        SampleRate = sampleRate,
        Channels = channels,
    };
    private float[] _temp = [];

    // Teardown gate. OwnAudio 3.1.7's AudioMixer.Stop() now joins the mix thread, but we keep this gate
    // as targeted defense-in-depth: a ReadSamples call can be mid-Callback when the host tears playback
    // down, and the callback reads memory-mapped SoundFont samples that Stop disposes. Reading those
    // after the mmap is released is a native use-after-free (AccessViolationException). _cbGate serializes
    // the callback against teardown; _stopping permanently blocks further callbacks once teardown begins
    // — so the host can dispose immediately without depending on the mixer's join timeout.
    private readonly object _cbGate = new();
    private volatile bool _stopping;

    public AudioCallback? Callback { get; set; }

    public override AudioConfig Config => _config;

    public override AudioStreamInfo StreamInfo => new AudioStreamInfo(
        channels: _config.Channels,
        sampleRate: _config.SampleRate,
        duration: TimeSpan.Zero);

    public override double Position => 0;

    // Live (open-ended) source — Duration of zero signals "unknown / streaming".
    public override double Duration => 0;

    public override bool IsEndOfStream => false;

    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        int sampleCount = frameCount * _config.Channels;
        Span<float> output = buffer.Slice(0, sampleCount);

        // Cheap pre-check (also avoids taking the gate once teardown has begun).
        if (_stopping || State != AudioState.Playing || Callback is null)
        {
            output.Clear();
            return frameCount;
        }

        if (_temp.Length < sampleCount)
            _temp = new float[sampleCount];

        Array.Clear(_temp, 0, sampleCount);
        // Hold the gate across the callback so DrainCallbacks() can wait for an in-flight call to
        // finish before the host disposes the SoundFont. Re-check _stopping inside the lock to close
        // the race where teardown set it after the pre-check above.
        lock (_cbGate)
        {
            if (_stopping || Callback is null) { output.Clear(); return frameCount; }
            Callback(_temp, frameCount);
        }
        _temp.AsSpan(0, sampleCount).CopyTo(output);
        return frameCount;
    }

    /// <summary>
    /// Permanently stop invoking <see cref="Callback"/> and block until any in-flight callback has
    /// returned. After this the host can safely dispose resources the callback touches (the
    /// memory-mapped SoundFont), even though OwnAudio's mix thread may still be alive and calling
    /// <see cref="ReadSamples"/> — those calls now just emit silence.
    /// </summary>
    public void DrainCallbacks()
    {
        _stopping = true;
        lock (_cbGate) { }   // waits out an in-flight callback; once acquired, none can start
    }

    public override bool Seek(double positionInSeconds) => false;
}
