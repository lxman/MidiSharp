using System;
using Ownaudio.Core;
using OwnaudioNET.Sources;

namespace MidiSharp.Synth.OwnAudio;

/// <summary>
/// OwnAudio callback adapter that pulls interleaved float frames from a user-supplied
/// <see cref="AudioCallback"/> via <see cref="StreamingSource"/>. The streaming pump calls
/// <see cref="RenderCallback"/>; we hand off to the synth's callback and copy the result
/// into the output span.
/// </summary>
internal sealed class SynthCallbackSource(int sampleRate, int channels)
{
    private float[] _temp = [];

    // Teardown gate. In OwnAudio 4.x, StreamingSource runs a pump loop on a background thread that invokes
    // RenderCallback to feed the Rust native mixer. When the host tears playback down, a RenderCallback call
    // can be mid-Callback reading memory-mapped SoundFont samples that Stop disposes. Reading those
    // after the mmap is released is a native use-after-free (AccessViolationException). _cbGate serializes
    // the callback against teardown; _stopping permanently blocks further callbacks once teardown begins
    // — so the host can dispose immediately without depending on the pump thread's teardown timing.
    private readonly object _cbGate = new();
    private volatile bool _stopping;

    public AudioCallback? Callback { get; set; }

    public StreamingSource CreateStreamingSource(int bufferSizeFrames)
    {
        var config = new AudioConfig
        {
            SampleRate = sampleRate,
            Channels = channels,
            BufferSize = bufferSizeFrames,
        };
        return new StreamingSource(RenderCallback, config);
    }

    private void RenderCallback(Span<float> buffer, int frameCount, long framePosition)
    {
        int sampleCount = frameCount * channels;
        Span<float> output = buffer.Slice(0, sampleCount);

        // Cheap pre-check (also avoids taking the gate once teardown has begun).
        if (_stopping || Callback is null)
        {
            output.Clear();
            return;
        }

        if (_temp.Length < sampleCount)
            _temp = new float[sampleCount];

        Array.Clear(_temp, 0, sampleCount);
        // Hold the gate across the callback so DrainCallbacks() can wait for an in-flight call to
        // finish before the host disposes the SoundFont. Re-check _stopping inside the lock to close
        // the race where teardown set it after the pre-check above.
        lock (_cbGate)
        {
            if (_stopping || Callback is null) { output.Clear(); return; }
            Callback(_temp, frameCount);
        }
        _temp.AsSpan(0, sampleCount).CopyTo(output);
    }

    /// <summary>
    /// Permanently stop invoking <see cref="Callback"/> and block until any in-flight callback has
    /// returned. After this the host can safely dispose resources the callback touches (the
    /// memory-mapped SoundFont), even though OwnAudio's pump thread may still be alive and calling
    /// <see cref="RenderCallback"/> — those calls now just emit silence.
    /// </summary>
    public void DrainCallbacks()
    {
        _stopping = true;
        lock (_cbGate) { }   // waits out an in-flight callback; once acquired, none can start
    }
}
