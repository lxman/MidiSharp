using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Determine the sample rate to open <paramref name="outputDeviceId"/> at — the rate the whole
    /// pipeline should then run at. Deterministic where possible (we ask the device, not guess):
    /// <list type="bullet">
    /// <item><b>PortAudio</b> (device id is a bare index, e.g. "23") is exact-format with no implicit
    /// resampling, so we read the device's advertised <c>defaultSampleRate</c> via PortAudio directly
    /// and return it — the pipeline runs there with zero resampling.</item>
    /// <item><b>MiniAudio</b> (device id "ma_output_N") opens shared-mode with a built-in converter, so
    /// any device accepts <paramref name="preferred"/> (MiniAudio resamples internally to the device's
    /// native rate) — we just return <paramref name="preferred"/>.</item>
    /// </list>
    /// Falls back to an empirical probe only if the PortAudio query is unavailable (e.g. the bundled
    /// library can't be reached), and to <paramref name="preferred"/> if even that can't run.
    /// </summary>
    /// <remarks>Only call when no audio is playing (the fallback probe transiently opens the engine).</remarks>
    public static int NegotiateSampleRate(string outputDeviceId, int preferred)
    {
        if (string.IsNullOrEmpty(outputDeviceId)) return preferred;

        // MiniAudio device ids look like "ma_output_N"; PortAudio's are bare device indices. Only the
        // PortAudio path needs (and can answer) a rate query — MiniAudio converts internally.
        if (!int.TryParse(outputDeviceId, out int paDeviceIndex))
            return preferred;

        int queried = PortAudioInterop.TryGetDefaultSampleRate(paDeviceIndex);
        if (queried > 0) return queried;

        // Query unavailable (library/init failure): fall back to the empirical probe, then preferred.
        return ProbeSampleRate(outputDeviceId, preferred);
    }

    // Last-ditch fallback when the PortAudio query can't run: transiently open the engine at candidate
    // rates and keep the first that the device accepts. Preferred first (keeps 48 kHz devices on the
    // no-resample fast path), then the other ubiquitous consumer/pro rates.
    private static int ProbeSampleRate(string outputDeviceId, int preferred)
    {
        var candidates = new List<int> { preferred };
        foreach (int r in new[] { 48000, 44100, 96000, 88200, 192000, 32000 })
            if (!candidates.Contains(r)) candidates.Add(r);

        lock (s_initLock)
        {
            // Don't probe while the engine is live (would tear it out from under an active stream).
            if (s_initRefCount != 0) return preferred;
            foreach (int rate in candidates)
                if (TryProbeDeviceRate(outputDeviceId, rate))
                    return rate;
        }
        return preferred;
    }

    // Transiently open the engine at `rate` and try to select the device; a negative selection code
    // (e.g. PortAudio -9997 paInvalidSampleRate) means this rate/device combination can't open. Always
    // shuts the engine back down so the caller's real init starts from a clean slate. Caller holds s_initLock.
    private static bool TryProbeDeviceRate(string outputDeviceId, int rate)
    {
        try
        {
            OwnaudioNet.Initialize(new AudioConfig
            {
                SampleRate = rate,
                Channels = 2,
                BufferSize = 1024,
                HostType = DefaultHostType,
                OutputDeviceId = outputDeviceId,
            });
            IAudioEngine? engine = OwnaudioNet.Engine?.UnderlyingEngine;
            if (engine is null) return false;
            List<AudioDeviceInfo> devices = engine.GetOutputDevices();
            for (int i = 0; i < devices.Count; i++)
                if (devices[i].DeviceId == outputDeviceId)
                    return engine.SetOutputDeviceByIndex(i) >= 0;
            return false;   // device vanished since enumeration
        }
        catch
        {
            return false;
        }
        finally
        {
            try { OwnaudioNet.Shutdown(); } catch { /* best-effort */ }
        }
    }

    // Direct PortAudio query for a device's advertised native rate. PortAudio (bundled with the app by
    // OwnAudioSharp at runtimes/&lt;rid&gt;/native/libportaudio) populates PaDeviceInfo.defaultSampleRate
    // from the device's mix format during enumeration — i.e. it already made the WASAPI GetMixFormat call
    // we'd otherwise make ourselves. We bind the same library by name (resolving to the same loaded
    // module OwnAudio uses) and read it. Pa_Initialize is reference-counted, so our transient init/term
    // pair coexists with OwnAudio's engine. All-or-nothing: any failure (wrong backend, lib missing,
    // not initialized) returns 0 and the caller falls back to the probe.
    private static class PortAudioInterop
    {
        private const string Lib = "libportaudio";

        [DllImport(Lib)] private static extern int Pa_Initialize();
        [DllImport(Lib)] private static extern int Pa_Terminate();
        [DllImport(Lib)] private static extern int Pa_GetDeviceCount();
        [DllImport(Lib)] private static extern IntPtr Pa_GetDeviceInfo(int device);

        // Layout must match PortAudio's PaDeviceInfo exactly (see OwnAudio's PaBinding.PaDeviceInfo).
        [StructLayout(LayoutKind.Sequential)]
        private struct PaDeviceInfo
        {
            public int StructVersion;
            public IntPtr Name;                       // const char*
            public int HostApi;
            public int MaxInputChannels;
            public int MaxOutputChannels;
            public double DefaultLowInputLatency;
            public double DefaultLowOutputLatency;
            public double DefaultHighInputLatency;
            public double DefaultHighOutputLatency;
            public double DefaultSampleRate;
        }

        /// <summary>The device's advertised default sample rate, or 0 if it can't be read.</summary>
        public static int TryGetDefaultSampleRate(int deviceIndex)
        {
            // Serialize against OwnAudio's own engine init/enumeration (shared process-wide PortAudio
            // state); only query when the engine isn't live.
            lock (s_initLock)
            {
                if (s_initRefCount != 0) return 0;
                bool initialized = false;
                try
                {
                    if (Pa_Initialize() != 0) return 0;   // paNoError == 0
                    initialized = true;
                    if (deviceIndex < 0 || deviceIndex >= Pa_GetDeviceCount()) return 0;
                    IntPtr p = Pa_GetDeviceInfo(deviceIndex);
                    if (p == IntPtr.Zero) return 0;
                    PaDeviceInfo info = Marshal.PtrToStructure<PaDeviceInfo>(p);
                    int rate = (int)Math.Round(info.DefaultSampleRate);
                    return rate > 0 ? rate : 0;
                }
                catch (DllNotFoundException) { return 0; }       // MiniAudio-only build / lib missing
                catch (EntryPointNotFoundException) { return 0; }
                catch { return 0; }
                finally
                {
                    if (initialized) { try { Pa_Terminate(); } catch { /* best-effort */ } }
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

        // Disable + drain the callback FIRST. OwnAudio's mix thread isn't joined by Stop()/Dispose(),
        // so without this an in-flight callback could keep reading the synth's memory-mapped SoundFont
        // after the caller disposes it — a native use-after-free (AccessViolationException) that takes
        // down the process. Once DrainCallbacks() returns, the callback can never run again, so the
        // caller is free to tear down the synth/session even though the mix thread may briefly linger.
        try { _source?.DrainCallbacks(); } catch { }

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
            // The engine is already up (another OwnAudioOutput holds it): just take a reference.
            if (s_initRefCount > 0) { s_initRefCount++; return; }

            // First bring-up. Do it transactionally: if Initialize, device selection, or Start
            // fails (e.g. the chosen device can't be opened at our format → PortAudio -9988), roll
            // the engine all the way back to uninitialized and rethrow. Otherwise a half-initialized
            // engine would be left at refCount>0 with a dead stream, and every later play would
            // short-circuit init and build on that corpse — the "first bad pick poisons everything"
            // crash. We only commit refCount once the engine is fully started.
            try
            {
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
                // Start (it refuses while the stream is running). Resolve the requested id and select
                // it here; a device that can't be opened fails fast with a clear message (below)
                // rather than leaving a dead stream for Start() to choke on with a cryptic code.
                if (!string.IsNullOrEmpty(outputDeviceId))
                    ApplyOutputDevice(outputDeviceId);

                OwnaudioNet.Start();
                s_initRefCount = 1;   // commit only after a fully successful bring-up
            }
            catch
            {
                try { OwnaudioNet.Shutdown(); } catch { /* best-effort rollback */ }
                s_initRefCount = 0;
                throw;
            }
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

    // Select the device whose DeviceId (as returned by GetOutputDevices) matches, on the live
    // engine. Must be called after Initialize and before the engine Start.
    //
    // We select by INDEX, not name: Windows enumerates each physical device once per audio API
    // (MME, DirectSound, WASAPI, WDM-KS), so device names are routinely duplicated (e.g. four
    // "Speaker (Jabra Engage 75)" entries with distinct ids). SetOutputDeviceByName would pick the
    // first by name — almost never the id the user chose. The id is unique, so we match on it and
    // pass its position to SetOutputDeviceByIndex, which indexes the very same engine device list.
    //
    // A device the backend can't open (negative return code) throws with a clear, actionable
    // message. This is common with WDM-KS and some WASAPI endpoints, which are exclusive-mode and/or
    // demand an exact format match: rejecting it here (so the caller rolls the engine back and
    // reports it) is far better than letting a dead stream reach Start() and surface as a raw code.
    // If the id no longer matches any enumerated device, we leave the engine on its default.
    private static void ApplyOutputDevice(string outputDeviceId)
    {
        IAudioEngine? engine = OwnaudioNet.Engine?.UnderlyingEngine;
        if (engine is null) return;
        List<AudioDeviceInfo> devices = engine.GetOutputDevices();
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].DeviceId != outputDeviceId) continue;
            int rc = engine.SetOutputDeviceByIndex(i);
            if (rc < 0)
                throw new InvalidOperationException(
                    $"Couldn't open the output device “{devices[i].Name}” (audio backend error {rc}). " +
                    "It may be in exclusive use by another app, or may not support the required format. " +
                    "Pick a different device or audio API (WASAPI shared-mode devices are the most reliable on Windows).");
            return;
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
