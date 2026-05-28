using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Synth.Windows;

/// <summary>
/// Windows audio output implementation using WinMM waveOut API.
/// </summary>
public sealed class WinMMAudioOutput : IAudioOutput
{
    private const int BufferCount = 3;
    private const int FramesPerBuffer = 1024;

    private IntPtr _waveOut;
    private readonly WaveHeader[] _headers;
    private readonly IntPtr[] _headerPtrs;
    private readonly byte[][] _buffers;
    private readonly GCHandle[] _bufferHandles;
    private readonly GCHandle[] _headerHandles;
    private readonly int _sampleRate;
    private readonly int _channels;
    private bool _isPlaying;
    private bool _disposed;
    private AudioCallback? _callback;
    private readonly float[] _tempBuffer;
    private WaveOutProc? _waveOutProc;
    private readonly object _lock = new();

    /// <inheritdoc />
    public int SampleRate => _sampleRate;

    /// <inheritdoc />
    public int Channels => _channels;

    /// <inheritdoc />
    public bool IsPlaying => _isPlaying;

    /// <summary>
    /// Creates a new WinMM audio output.
    /// </summary>
    /// <param name="sampleRate">Sample rate (default 44100)</param>
    /// <param name="channels">Number of channels (default 2 for stereo)</param>
    public WinMMAudioOutput(int sampleRate = 44100, int channels = 2)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _headers = new WaveHeader[BufferCount];
        _headerPtrs = new IntPtr[BufferCount];
        _buffers = new byte[BufferCount][];
        _bufferHandles = new GCHandle[BufferCount];
        _headerHandles = new GCHandle[BufferCount];
        _tempBuffer = new float[FramesPerBuffer * channels];
    }

    /// <inheritdoc />
    public void SetCallback(AudioCallback callback)
    {
        _callback = callback;
    }

    /// <inheritdoc />
    public void Start()
    {
        ThrowIfDisposed();
        if (_isPlaying) return;

        // Set up wave format - 16-bit PCM
        var format = new WaveFormatEx
        {
            wFormatTag = 1, // WAVE_FORMAT_PCM
            nChannels = (ushort)_channels,
            nSamplesPerSec = (uint)_sampleRate,
            wBitsPerSample = 16,
            nBlockAlign = (ushort)(_channels * 2),
            nAvgBytesPerSec = (uint)(_sampleRate * _channels * 2),
            cbSize = 0
        };

        // Create callback delegate and prevent GC collection
        _waveOutProc = WaveOutCallback;

        // Open wave output device
        int result = NativeMethods.waveOutOpen(
            out _waveOut,
            NativeMethods.WAVE_MAPPER,
            ref format,
            _waveOutProc,
            IntPtr.Zero,
            NativeMethods.CALLBACK_FUNCTION);

        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to open wave output. Error: {result}");
        }

        // Allocate and prepare buffers
        int bufferSize = FramesPerBuffer * _channels * sizeof(short);
        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = new byte[bufferSize];
            _bufferHandles[i] = GCHandle.Alloc(_buffers[i], GCHandleType.Pinned);

            _headers[i] = new WaveHeader
            {
                lpData = _bufferHandles[i].AddrOfPinnedObject(),
                dwBufferLength = (uint)bufferSize,
                dwFlags = 0,
                dwLoops = 0
            };

            _headerHandles[i] = GCHandle.Alloc(_headers[i], GCHandleType.Pinned);
            _headerPtrs[i] = _headerHandles[i].AddrOfPinnedObject();

            result = NativeMethods.waveOutPrepareHeader(_waveOut, _headerPtrs[i], (uint)Marshal.SizeOf<WaveHeader>());
            if (result != 0)
            {
                Cleanup();
                throw new InvalidOperationException($"Failed to prepare wave header. Error: {result}");
            }

            // Fill and queue the buffer
            FillBuffer(i);
            result = NativeMethods.waveOutWrite(_waveOut, _headerPtrs[i], (uint)Marshal.SizeOf<WaveHeader>());
            if (result != 0)
            {
                Cleanup();
                throw new InvalidOperationException($"Failed to write wave buffer. Error: {result}");
            }
        }

        _isPlaying = true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_isPlaying) return;

        lock (_lock)
        {
            _isPlaying = false;

            if (_waveOut != IntPtr.Zero)
            {
                NativeMethods.waveOutReset(_waveOut);
            }

            Cleanup();
        }
    }

    private void Cleanup()
    {
        if (_waveOut != IntPtr.Zero)
        {
            for (int i = 0; i < BufferCount; i++)
            {
                if (_headerPtrs[i] != IntPtr.Zero)
                {
                    NativeMethods.waveOutUnprepareHeader(_waveOut, _headerPtrs[i], (uint)Marshal.SizeOf<WaveHeader>());
                }

                if (_headerHandles[i].IsAllocated)
                    _headerHandles[i].Free();

                if (_bufferHandles[i].IsAllocated)
                    _bufferHandles[i].Free();

                _headerPtrs[i] = IntPtr.Zero;
            }

            NativeMethods.waveOutClose(_waveOut);
            _waveOut = IntPtr.Zero;
        }
    }

    private void WaveOutCallback(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        if (uMsg == NativeMethods.WOM_DONE)
        {
            HandleBufferDone(dwParam1);
        }
    }

    private void HandleBufferDone(IntPtr headerPtr)
    {
        if (!_isPlaying) return;

        lock (_lock)
        {
            if (!_isPlaying || _waveOut == IntPtr.Zero) return;

            // Find which buffer completed
            int bufferIndex = -1;
            for (int i = 0; i < BufferCount; i++)
            {
                if (_headerPtrs[i] == headerPtr)
                {
                    bufferIndex = i;
                    break;
                }
            }

            if (bufferIndex < 0) return;

            // Refill and requeue the buffer
            FillBuffer(bufferIndex);
            NativeMethods.waveOutWrite(_waveOut, _headerPtrs[bufferIndex], (uint)Marshal.SizeOf<WaveHeader>());
        }
    }

    private void FillBuffer(int bufferIndex)
    {
        int frames = FramesPerBuffer;

        // Call user callback to fill temp buffer with floats
        if (_callback != null)
        {
            Array.Clear(_tempBuffer, 0, _tempBuffer.Length);
            _callback(_tempBuffer, frames);
        }
        else
        {
            Array.Clear(_tempBuffer, 0, _tempBuffer.Length);
        }

        // Convert float samples to 16-bit PCM
        int sampleCount = frames * _channels;
        unsafe
        {
            fixed (byte* bufferPtr = _buffers[bufferIndex])
            {
                short* pcm = (short*)bufferPtr;
                for (int i = 0; i < sampleCount; i++)
                {
                    float sample = _tempBuffer[i];
                    // Clamp and convert to 16-bit
                    sample = Math.Clamp(sample, -1.0f, 1.0f);
                    pcm[i] = (short)(sample * 32767);
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WinMMAudioOutput));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

#region Native Types

[StructLayout(LayoutKind.Sequential)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveHeader
{
    public IntPtr lpData;
    public uint dwBufferLength;
    public uint dwBytesRecorded;
    public IntPtr dwUser;
    public uint dwFlags;
    public uint dwLoops;
    public IntPtr lpNext;
    public IntPtr reserved;
}

internal delegate void WaveOutProc(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

#endregion

#region Native Methods

internal static class NativeMethods
{
    private const string WinMM = "winmm.dll";

    public const uint WAVE_MAPPER = unchecked((uint)-1);
    public const uint CALLBACK_FUNCTION = 0x00030000;
    public const uint WOM_DONE = 0x3BD;

    [DllImport(WinMM)]
    public static extern int waveOutOpen(
        out IntPtr phwo,
        uint uDeviceID,
        ref WaveFormatEx pwfx,
        WaveOutProc dwCallback,
        IntPtr dwInstance,
        uint fdwOpen);

    [DllImport(WinMM)]
    public static extern int waveOutClose(IntPtr hwo);

    [DllImport(WinMM)]
    public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport(WinMM)]
    public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport(WinMM)]
    public static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport(WinMM)]
    public static extern int waveOutReset(IntPtr hwo);
}

#endregion
