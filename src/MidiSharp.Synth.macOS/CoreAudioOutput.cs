using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MidiSharp.Synth.macOS;

/// <summary>
/// macOS audio output implementation using Core Audio's AudioQueue Services.
/// </summary>
public sealed class CoreAudioOutput : IAudioOutput
{
    private const int BufferCount = 3;
    private const int FramesPerBuffer = 1024;

    private IntPtr _audioQueue;
    private readonly IntPtr[] _buffers;
    private readonly int _sampleRate;
    private readonly int _channels;
    private bool _isPlaying;
    private bool _disposed;
    private AudioCallback? _callback;
    private GCHandle _callbackHandle;
    private readonly float[] _tempBuffer;
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public int SampleRate => _sampleRate;

    /// <inheritdoc />
    public int Channels => _channels;

    /// <inheritdoc />
    public bool IsPlaying => _isPlaying;

    /// <summary>
    /// Creates a new Core Audio output.
    /// </summary>
    /// <param name="sampleRate">Sample rate (default 44100)</param>
    /// <param name="channels">Number of channels (default 2 for stereo)</param>
    public CoreAudioOutput(int sampleRate = 44100, int channels = 2)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _buffers = new IntPtr[BufferCount];
        _tempBuffer = new float[FramesPerBuffer * channels];
    }

    /// <inheritdoc />
    public void SetCallback(AudioCallback callback)
    {
        _callback = callback;
    }

    /// <inheritdoc />
    public unsafe void Start()
    {
        ThrowIfDisposed();
        if (_isPlaying) return;

        // Create audio queue for output
        var format = new AudioStreamBasicDescription
        {
            SampleRate = _sampleRate,
            FormatID = AudioFormatType.LinearPCM,
            FormatFlags = AudioFormatFlags.Float | AudioFormatFlags.Packed | AudioFormatFlags.NativeEndian,
            BytesPerPacket = (uint)(_channels * sizeof(float)),
            FramesPerPacket = 1,
            BytesPerFrame = (uint)(_channels * sizeof(float)),
            ChannelsPerFrame = (uint)_channels,
            BitsPerChannel = 32
        };

        // Pin this object for the callback
        _callbackHandle = GCHandle.Alloc(this);

        int status = NativeMethods.AudioQueueNewOutput(
            ref format,
            &AudioQueueCallbackProc,
            GCHandle.ToIntPtr(_callbackHandle),
            IntPtr.Zero, // Run loop
            IntPtr.Zero, // Run loop mode
            0,
            out _audioQueue);

        if (status != 0)
        {
            if (_callbackHandle.IsAllocated)
                _callbackHandle.Free();
            throw new InvalidOperationException($"Failed to create audio queue. Error: {status}");
        }

        // Allocate and enqueue buffers
        int bufferSize = FramesPerBuffer * _channels * sizeof(float);
        for (int i = 0; i < BufferCount; i++)
        {
            status = NativeMethods.AudioQueueAllocateBuffer(_audioQueue, (uint)bufferSize, out _buffers[i]);
            if (status != 0)
            {
                Cleanup();
                throw new InvalidOperationException($"Failed to allocate audio buffer. Error: {status}");
            }

            // Prime the buffer
            FillBuffer(_buffers[i]);
            status = NativeMethods.AudioQueueEnqueueBuffer(_audioQueue, _buffers[i], 0, IntPtr.Zero);
            if (status == 0) continue;
            Cleanup();
            throw new InvalidOperationException($"Failed to enqueue audio buffer. Error: {status}");
        }

        // Start playback
        status = NativeMethods.AudioQueueStart(_audioQueue, IntPtr.Zero);
        if (status != 0)
        {
            Cleanup();
            throw new InvalidOperationException($"Failed to start audio queue. Error: {status}");
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

            if (_audioQueue != IntPtr.Zero)
            {
                NativeMethods.AudioQueueStop(_audioQueue, true);
            }

            Cleanup();
        }
    }

    private void Cleanup()
    {
        if (_audioQueue != IntPtr.Zero)
        {
            for (int i = 0; i < BufferCount; i++)
            {
                if (_buffers[i] == IntPtr.Zero) continue;
                NativeMethods.AudioQueueFreeBuffer(_audioQueue, _buffers[i]);
                _buffers[i] = IntPtr.Zero;
            }

            NativeMethods.AudioQueueDispose(_audioQueue, true);
            _audioQueue = IntPtr.Zero;
        }

        if (_callbackHandle.IsAllocated)
            _callbackHandle.Free();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void AudioQueueCallbackProc(IntPtr userData, IntPtr audioQueue, IntPtr buffer)
    {
        var handle = GCHandle.FromIntPtr(userData);
        if (!handle.IsAllocated) return;

        var output = (CoreAudioOutput)handle.Target!;
        output.HandleCallback(buffer);
    }

    private void HandleCallback(IntPtr buffer)
    {
        if (!_isPlaying) return;

        lock (_lock)
        {
            if (!_isPlaying || _audioQueue == IntPtr.Zero) return;

            FillBuffer(buffer);

            int status = NativeMethods.AudioQueueEnqueueBuffer(_audioQueue, buffer, 0, IntPtr.Zero);
            if (status != 0)
            {
                // Log error but don't throw - we're in a callback
            }
        }
    }

    private unsafe void FillBuffer(IntPtr buffer)
    {
        // Get buffer info
        var audioBuffer = (AudioQueueBuffer*)buffer;
        float* data = (float*)audioBuffer->AudioData;
        int frames = FramesPerBuffer;

        // Call user callback to fill temp buffer
        if (_callback != null)
        {
            Array.Clear(_tempBuffer, 0, _tempBuffer.Length);
            _callback(_tempBuffer, frames);
        }
        else
        {
            Array.Clear(_tempBuffer, 0, _tempBuffer.Length);
        }

        // Copy to audio buffer
        int sampleCount = frames * _channels;
        for (int i = 0; i < sampleCount; i++)
        {
            data[i] = _tempBuffer[i];
        }

        audioBuffer->AudioDataByteSize = (uint)(sampleCount * sizeof(float));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) ObjectDisposedException.ThrowIf(_disposed, this);
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
internal struct AudioStreamBasicDescription
{
    public double SampleRate;
    public AudioFormatType FormatID;
    public AudioFormatFlags FormatFlags;
    public uint BytesPerPacket;
    public uint FramesPerPacket;
    public uint BytesPerFrame;
    public uint ChannelsPerFrame;
    public uint BitsPerChannel;
    public uint Reserved;
}

internal enum AudioFormatType : uint
{
    LinearPCM = 0x6C70636D // 'lpcm'
}

[Flags]
internal enum AudioFormatFlags : uint
{
    Float = 1 << 0,
    BigEndian = 1 << 1,
    SignedInteger = 1 << 2,
    Packed = 1 << 3,
    AlignedHigh = 1 << 4,
    NonInterleaved = 1 << 5,
    NonMixable = 1 << 6,
    NativeEndian = 0, // Little endian on ARM/Intel
    Canonical = Float | Packed | NativeEndian
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioQueueBuffer
{
    public uint AudioDataBytesCapacity;
    public IntPtr AudioData;
    public uint AudioDataByteSize;
    public IntPtr UserData;
    public uint PacketDescriptionCapacity;
    public IntPtr PacketDescriptions;
    public uint PacketDescriptionCount;
}

#endregion

#region Native Methods

internal static unsafe class NativeMethods
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    [DllImport(AudioToolbox)]
    public static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription format,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> callback,
        IntPtr userData,
        IntPtr runLoop,
        IntPtr runLoopMode,
        uint flags,
        out IntPtr audioQueue);

    [DllImport(AudioToolbox)]
    public static extern int AudioQueueDispose(IntPtr audioQueue, bool immediate);

    [DllImport(AudioToolbox)]
    public static extern int AudioQueueAllocateBuffer(IntPtr audioQueue, uint bufferByteSize, out IntPtr buffer);

    [DllImport(AudioToolbox)]
    public static extern int AudioQueueFreeBuffer(IntPtr audioQueue, IntPtr buffer);

    [DllImport(AudioToolbox)]
    public static extern int AudioQueueEnqueueBuffer(IntPtr audioQueue, IntPtr buffer, uint numPacketDescs, IntPtr packetDescs);

    [DllImport(AudioToolbox)]
    public static extern int AudioQueueStart(IntPtr audioQueue, IntPtr startTime);

    [DllImport(AudioToolbox)]
    public static extern int AudioQueueStop(IntPtr audioQueue, bool immediate);

    [DllImport(AudioToolbox)]
    public static extern int AudioQueuePause(IntPtr audioQueue);
}

#endregion
