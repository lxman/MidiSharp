using System;
using System.Runtime.InteropServices;
using MidiSharp.Abstractions;

namespace MidiSharp.Output.Windows;

/// <summary>
/// Windows MIDI output implementation using the Windows Multimedia API (winmm.dll).
/// </summary>
public sealed class WindowsMidiOutput : IMidiOutput
{
    private IntPtr _handle;
    private bool _isOpen;
    private readonly int _deviceIndex;
    private bool _disposed;

    public WindowsMidiOutput(int deviceIndex = 0)
    {
        _deviceIndex = deviceIndex;
    }

    public bool IsOpen => _isOpen;

    public void Open()
    {
        ThrowIfDisposed();
        if (_isOpen) return;

        int result = NativeMethods.midiOutOpen(out _handle, _deviceIndex, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to open MIDI output device {_deviceIndex}. Error code: {result}");
        }

        _isOpen = true;
    }

    public void Close()
    {
        if (!_isOpen) return;

        AllNotesOff();
        NativeMethods.midiOutClose(_handle);
        _handle = IntPtr.Zero;
        _isOpen = false;
    }

    public void SendShortMessage(byte status, byte data1, byte data2)
    {
        ThrowIfNotOpen();
        int message = status | (data1 << 8) | (data2 << 16);
        int result = NativeMethods.midiOutShortMsg(_handle, message);
        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to send MIDI message. Error code: {result}");
        }
    }

    public void SendShortMessage(byte status, byte data1)
    {
        ThrowIfNotOpen();
        int message = status | (data1 << 8);
        int result = NativeMethods.midiOutShortMsg(_handle, message);
        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to send MIDI message. Error code: {result}");
        }
    }

    public void SendSysEx(ReadOnlySpan<byte> data)
    {
        ThrowIfNotOpen();

        // Allocate unmanaged memory for the data
        IntPtr dataPtr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data.ToArray(), 0, dataPtr, data.Length);

            var header = new MIDIHDR
            {
                lpData = dataPtr,
                dwBufferLength = (uint)data.Length,
                dwBytesRecorded = (uint)data.Length,
                dwFlags = 0
            };

            int headerSize = Marshal.SizeOf<MIDIHDR>();
            IntPtr headerPtr = Marshal.AllocHGlobal(headerSize);
            try
            {
                Marshal.StructureToPtr(header, headerPtr, false);

                int result = NativeMethods.midiOutPrepareHeader(_handle, headerPtr, headerSize);
                if (result != 0)
                {
                    throw new InvalidOperationException($"Failed to prepare MIDI header. Error code: {result}");
                }

                result = NativeMethods.midiOutLongMsg(_handle, headerPtr, headerSize);
                if (result != 0)
                {
                    NativeMethods.midiOutUnprepareHeader(_handle, headerPtr, headerSize);
                    throw new InvalidOperationException($"Failed to send SysEx message. Error code: {result}");
                }

                // Wait for the message to be sent (in a real implementation, you'd use a callback)
                // For simplicity, we'll just unprepare immediately
                NativeMethods.midiOutUnprepareHeader(_handle, headerPtr, headerSize);
            }
            finally
            {
                Marshal.FreeHGlobal(headerPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(dataPtr);
        }
    }

    public void AllNotesOff()
    {
        if (!_isOpen) return;

        // Send All Notes Off (CC 123) on all channels
        for (int channel = 0; channel < 16; channel++)
        {
            int message = (0xB0 | channel) | (123 << 8) | (0 << 16);
            NativeMethods.midiOutShortMsg(_handle, message);
        }
    }

    public void Reset()
    {
        if (!_isOpen) return;

        NativeMethods.midiOutReset(_handle);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsMidiOutput));
    }

    private void ThrowIfNotOpen()
    {
        ThrowIfDisposed();
        if (!_isOpen)
            throw new InvalidOperationException("MIDI output is not open");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    #region Native Structures

    [StructLayout(LayoutKind.Sequential)]
    private struct MIDIHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public IntPtr lpNext;
        public IntPtr reserved;
        public uint dwOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public IntPtr[] dwReserved;
    }

    #endregion
}

/// <summary>
/// P/Invoke declarations for Windows Multimedia API.
/// </summary>
internal static class NativeMethods
{
    private const string WinMM = "winmm.dll";

    [DllImport(WinMM)]
    public static extern int midiOutGetNumDevs();

    [DllImport(WinMM, CharSet = CharSet.Auto)]
    public static extern int midiOutGetDevCaps(int deviceId, out MIDIOUTCAPS caps, int size);

    [DllImport(WinMM)]
    public static extern int midiOutOpen(out IntPtr handle, int deviceId, IntPtr callback, IntPtr instance, int flags);

    [DllImport(WinMM)]
    public static extern int midiOutClose(IntPtr handle);

    [DllImport(WinMM)]
    public static extern int midiOutShortMsg(IntPtr handle, int message);

    [DllImport(WinMM)]
    public static extern int midiOutLongMsg(IntPtr handle, IntPtr header, int headerSize);

    [DllImport(WinMM)]
    public static extern int midiOutPrepareHeader(IntPtr handle, IntPtr header, int headerSize);

    [DllImport(WinMM)]
    public static extern int midiOutUnprepareHeader(IntPtr handle, IntPtr header, int headerSize);

    [DllImport(WinMM)]
    public static extern int midiOutReset(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MIDIOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }
}
