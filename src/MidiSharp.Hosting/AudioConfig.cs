namespace MidiSharp.Hosting;

/// <summary>
/// The audio contract a plugin is activated against: sample rate, the largest block it will be asked to
/// process, and the channel count of its stereo (or N-channel) bus. A hosted plugin allocates its
/// realtime buffers once from this, sized to <see cref="MaxBlockFrames"/>, and re-activates if any of
/// these change.
/// </summary>
public readonly record struct AudioConfig(int SampleRate, int MaxBlockFrames, int ChannelCount = 2);
