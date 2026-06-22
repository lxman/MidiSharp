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

        if (State != AudioState.Playing || Callback is null)
        {
            output.Clear();
            return frameCount;
        }

        if (_temp.Length < sampleCount)
            _temp = new float[sampleCount];

        Array.Clear(_temp, 0, sampleCount);
        Callback(_temp, frameCount);
        _temp.AsSpan(0, sampleCount).CopyTo(output);
        return frameCount;
    }

    public override bool Seek(double positionInSeconds) => false;
}
