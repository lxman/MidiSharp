using System;
using Ownaudio.Core;
using OwnaudioNET;
using OwnaudioNET.Mixing;

namespace MidiSharp.Synth.OwnAudio;

/// <summary>
/// Cross-platform audio output for the MidiSharp synthesizer, backed by OwnAudioSharp.
/// Wraps an OwnAudio AudioMixer plus an internal callback-driven source so the synth
/// can deliver float frames through PortAudio/MiniAudio on Windows, Linux, and macOS.
/// </summary>
public sealed class OwnAudioOutput : IAudioOutput
{
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _bufferSizeFrames;

    private AudioMixer? _mixer;
    private SynthCallbackSource? _source;
    private AudioCallback? _callback;
    private bool _isPlaying;
    private bool _disposed;

    // Reference-counted process-wide init/teardown of OwnaudioNet so multiple
    // OwnAudioOutput instances (e.g. in tests) don't tear the engine out from under each other.
    private static readonly object s_initLock = new();
    private static int s_initRefCount;

    /// <inheritdoc />
    public int SampleRate => _sampleRate;

    /// <inheritdoc />
    public int Channels => _channels;

    /// <inheritdoc />
    public bool IsPlaying => _isPlaying;

    /// <summary>
    /// Creates a new OwnAudio-backed audio output.
    /// </summary>
    /// <param name="sampleRate">Output sample rate in Hz.</param>
    /// <param name="channels">Channel count (2 = stereo). Must match the synth's interleaved output.</param>
    /// <param name="bufferSizeFrames">Audio engine buffer size in frames; smaller = lower latency.</param>
    public OwnAudioOutput(int sampleRate = 44100, int channels = 2, int bufferSizeFrames = 1024)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _bufferSizeFrames = bufferSizeFrames;
    }

    /// <inheritdoc />
    public void SetCallback(AudioCallback callback)
    {
        _callback = callback;
        if (_source != null)
            _source.Callback = callback;
    }

    /// <inheritdoc />
    public void Start()
    {
        ThrowIfDisposed();
        if (_isPlaying) return;

        EnsureEngineInitialized(_sampleRate, _channels, _bufferSizeFrames);

        var engine = OwnaudioNet.Engine!.UnderlyingEngine;
        _mixer = new AudioMixer(engine, bufferSizeInFrames: _bufferSizeFrames);
        _source = new SynthCallbackSource(_sampleRate, _channels) { Callback = _callback };
        _mixer.AddSource(_source);
        _mixer.Start();
        _source.Play();
        _isPlaying = true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_isPlaying) return;
        _isPlaying = false;

        try { _source?.Stop(); } catch { }
        try { _mixer?.Stop(); } catch { }
        try { _mixer?.Dispose(); } catch { }
        try { _source?.Dispose(); } catch { }
        _mixer = null;
        _source = null;

        ReleaseEngine();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OwnAudioOutput));
    }

    private static void EnsureEngineInitialized(int sampleRate, int channels, int bufferSizeFrames)
    {
        lock (s_initLock)
        {
            if (s_initRefCount++ > 0) return;

            var config = new AudioConfig
            {
                SampleRate = sampleRate,
                Channels = channels,
                BufferSize = bufferSizeFrames,
                HostType = EngineHostType.None,
            };
            OwnaudioNet.Initialize(config);
            OwnaudioNet.Start();
        }
    }

    private static void ReleaseEngine()
    {
        lock (s_initLock)
        {
            if (s_initRefCount == 0) return;
            if (--s_initRefCount > 0) return;
            try { OwnaudioNet.Stop(); } catch { }
            try { OwnaudioNet.Shutdown(); } catch { }
        }
    }
}
