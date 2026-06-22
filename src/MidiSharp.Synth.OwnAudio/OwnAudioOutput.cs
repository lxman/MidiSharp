using System;
using System.Collections.Generic;
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
    private readonly string? _outputDeviceId;

    private AudioMixer? _mixer;
    private SynthCallbackSource? _source;
    private AudioCallback? _callback;
    private bool _isPlaying;
    private bool _disposed;

    // Reference-counted process-wide init/teardown of OwnaudioNet so multiple
    // OwnAudioOutput instances (e.g. in tests) don't tear the engine out from under each other.
    private static readonly object s_initLock = new();
    private static int s_initRefCount;

    // Linux default is HostType.None → PortAudio opens its platform-default host API (ALSA),
    // i.e. the ALSA "default" PCM, which routes transparently on bare ALSA, PulseAudio, AND
    // PipeWire, and shows up as a normal sink-input the host can re-route. Forcing JACK was
    // wrong on desktops: PortAudio-JACK enumerates JACK *clients* (e.g. "GNOME Settings") as
    // "devices" and can't target a sink — output just follows the default. Pro-audio users who
    // genuinely want a native JACK client can opt in with MIDISHARP_AUDIO_JACK=1. (MiniAudio
    // ignores HostType entirely; non-Linux platforms get None → WASAPI / CoreAudio.)
    private static readonly EngineHostType DefaultHostType =
        OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("MIDISHARP_AUDIO_JACK") == "1"
            ? EngineHostType.JACK
            : EngineHostType.None;

    private const string PipeWireAlsaEnvVar = "PIPEWIRE_ALSA";

    /// <summary>
    /// PipeWire <c>node.name</c> given to our Linux playback stream. A host can locate exactly this
    /// stream (e.g. to route it to a chosen sink) via <c>pactl list sink-inputs</c>. Linux only.
    /// </summary>
    public const string LinuxPipeWireNodeName = "MidiSharp-Player";

    /// <summary>PipeWire <c>application.name</c> for our Linux playback stream (shown in mixers).</summary>
    public const string LinuxPipeWireAppName = "MidiSharp";

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
    /// <param name="outputDeviceId">
    /// Output device id from <see cref="GetOutputDevices"/> (an <c>AudioDeviceInfo.DeviceId</c>),
    /// or null for the system default. Applied via <c>SetOutputDeviceByName</c> after engine init
    /// and before start (OwnAudio ignores <c>AudioConfig.OutputDeviceId</c> at Initialize).
    /// </param>
    public OwnAudioOutput(int sampleRate = 44100, int channels = 2, int bufferSizeFrames = 1024, string? outputDeviceId = null)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _bufferSizeFrames = bufferSizeFrames;
        _outputDeviceId = outputDeviceId;
    }

    /// <summary>A selectable audio output device — a leak-free view of OwnAudio's AudioDeviceInfo.</summary>
    public sealed record OutputDevice(string Id, string Name, string EngineName, bool IsDefault);

    /// <summary>
    /// Enumerate the available audio output devices. The OwnAudio engine must be
    /// initialized to enumerate, so when nothing is playing this does a transient
    /// init/shutdown; when a stream is already running it queries the live engine.
    /// </summary>
    public static IReadOnlyList<OutputDevice> GetOutputDevices()
    {
        lock (s_initLock)
        {
            bool transient = s_initRefCount == 0;
            if (transient)
            {
                OwnaudioNet.Initialize(new AudioConfig
                {
                    // Must match the JACK graph rate (48k) or PortAudio-JACK enumeration
                    // fails with paInvalidSampleRate and OwnAudio falls back to MiniAudio,
                    // listing ALSA devices whose ids the JACK playback path can't use.
                    SampleRate = 48000,
                    Channels = 2,
                    BufferSize = 1024,
                    HostType = DefaultHostType,
                });
            }
            try
            {
                var devices = new List<OutputDevice>();
                foreach (AudioDeviceInfo d in OwnaudioNet.GetOutputDevices())
                {
                    if (!d.IsOutput) continue;
                    devices.Add(new OutputDevice(d.DeviceId, d.Name, d.EngineName, d.IsDefault));
                }
                return devices;
            }
            finally
            {
                if (transient)
                {
                    try { OwnaudioNet.Shutdown(); } catch { /* best-effort teardown */ }
                }
            }
        }
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

        EnsureEngineInitialized(_sampleRate, _channels, _bufferSizeFrames, _outputDeviceId);

        IAudioEngine engine = OwnaudioNet.Engine!.UnderlyingEngine;
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

    private static void EnsureEngineInitialized(int sampleRate, int channels, int bufferSizeFrames, string? outputDeviceId)
    {
        lock (s_initLock)
        {
            if (s_initRefCount++ > 0) return;

            // Linux/PipeWire (and the PipeWire ALSA plugin): give our playback stream a stable,
            // app-identifiable name BEFORE the engine opens the PCM. This makes the stream show as
            // "MidiSharp" in mixers (not "dotnet"), gives it its own module-stream-restore entry,
            // and lets a host (e.g. MidiSharp.Server) find and route exactly this stream. We set the
            // REAL C environment via libc setenv — Environment.SetEnvironmentVariable updates only the
            // CLR's view and the native ALSA plugin's getenv would never see it. We don't override a
            // value the user already supplied.
            if (OperatingSystem.IsLinux() &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PipeWireAlsaEnvVar)))
            {
                LibcEnv.TrySetEnv(PipeWireAlsaEnvVar,
                    $"{{ \"node.name\": \"{LinuxPipeWireNodeName}\", \"application.name\": \"{LinuxPipeWireAppName}\" }}");
            }

            var config = new AudioConfig
            {
                SampleRate = sampleRate,
                Channels = channels,
                BufferSize = bufferSizeFrames,
                HostType = DefaultHostType,
                OutputDeviceId = outputDeviceId,   // null = system default
            };
            OwnaudioNet.Initialize(config);

            // OwnAudio 3.1.3 does NOT honor AudioConfig.OutputDeviceId at Initialize() on either
            // the PortAudio or MiniAudio backend (the device-id resolution lives only in the
            // device-switch path, not the initial open). The device only takes effect via
            // SetOutputDeviceByName/ByIndex, which must run AFTER Initialize and BEFORE the engine
            // Start (it refuses while the stream is running). Resolve the requested id to a device
            // name and apply it here; on failure we fall back to the system default.
            if (!string.IsNullOrEmpty(outputDeviceId))
                ApplyOutputDevice(outputDeviceId);

            OwnaudioNet.Start();
        }
    }

    // Sets a variable in the real C environment so native code (the PipeWire ALSA plugin) sees it
    // via getenv. .NET's Environment.SetEnvironmentVariable only updates the managed view and would
    // not be visible to the native plugin. Linux only; best-effort.
    private static class LibcEnv
    {
        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int setenv(string name, string value, int overwrite);

        public static void TrySetEnv(string name, string value)
        {
            try { setenv(name, value, 1); } catch { /* non-Linux / no libc → no-op */ }
        }
    }

    // Resolve a DeviceId (as returned by GetOutputDevices) to its device name and select it on
    // the live engine. Must be called after Initialize and before the engine Start. Best-effort:
    // if the id no longer matches an enumerated device, or the engine rejects it, we leave the
    // engine on its default device rather than fail the whole playback.
    private static void ApplyOutputDevice(string outputDeviceId)
    {
        try
        {
            var engine = OwnaudioNet.Engine;
            if (engine is null) return;
            foreach (var d in engine.UnderlyingEngine.GetOutputDevices())
            {
                if (d.DeviceId != outputDeviceId) continue;
                engine.SetOutputDeviceByName(d.Name);
                return;
            }
        }
        catch
        {
            // Best-effort device selection — fall back to the system default.
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
