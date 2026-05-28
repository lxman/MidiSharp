using System;
using System.Runtime.InteropServices;
using System.Text;
using MidiSharp.Abstractions;

namespace MidiSharp.Output.macOS;

/// <summary>
/// macOS MIDI output implementation using Core MIDI framework.
/// </summary>
public sealed class CoreMidiOutput : IMidiOutput
{
    private IntPtr _client;
    private IntPtr _outputPort;
    private IntPtr _destination;
    private bool _isOpen;
    private readonly int _deviceIndex;
    private bool _disposed;

    public CoreMidiOutput(int deviceIndex = 0)
    {
        _deviceIndex = deviceIndex;
    }

    public bool IsOpen => _isOpen;

    /// <summary>
    /// Gets the number of available MIDI destinations.
    /// </summary>
    public static int GetDestinationCount()
    {
        return NativeMethods.MIDIGetNumberOfDestinations();
    }

    /// <summary>
    /// Gets the name of a MIDI destination.
    /// </summary>
    public static string? GetDestinationName(int index)
    {
        var dest = NativeMethods.MIDIGetDestination(index);
        if (dest == IntPtr.Zero) return null;

        IntPtr nameRef;
        int status = NativeMethods.MIDIObjectGetStringProperty(dest, NativeMethods.kMIDIPropertyName, out nameRef);
        if (status != 0 || nameRef == IntPtr.Zero) return $"Device {index}";

        return NativeMethods.CFStringGetCString(nameRef);
    }

    public void Open()
    {
        ThrowIfDisposed();
        if (_isOpen) return;

        // Create MIDI client
        int status = NativeMethods.MIDIClientCreate(
            NativeMethods.CreateCFString("MidiSharp"),
            IntPtr.Zero,
            IntPtr.Zero,
            out _client);

        if (status != 0)
        {
            throw new InvalidOperationException($"Failed to create MIDI client. Error code: {status}");
        }

        // Create output port
        status = NativeMethods.MIDIOutputPortCreate(
            _client,
            NativeMethods.CreateCFString("MidiSharp Output"),
            out _outputPort);

        if (status != 0)
        {
            NativeMethods.MIDIClientDispose(_client);
            _client = IntPtr.Zero;
            throw new InvalidOperationException($"Failed to create MIDI output port. Error code: {status}");
        }

        // Get destination
        int numDestinations = NativeMethods.MIDIGetNumberOfDestinations();
        if (numDestinations == 0)
        {
            NativeMethods.MIDIPortDispose(_outputPort);
            NativeMethods.MIDIClientDispose(_client);
            _outputPort = IntPtr.Zero;
            _client = IntPtr.Zero;
            throw new InvalidOperationException("No MIDI destinations available");
        }

        if (_deviceIndex >= numDestinations)
        {
            NativeMethods.MIDIPortDispose(_outputPort);
            NativeMethods.MIDIClientDispose(_client);
            _outputPort = IntPtr.Zero;
            _client = IntPtr.Zero;
            throw new ArgumentOutOfRangeException(nameof(_deviceIndex),
                $"Device index {_deviceIndex} is out of range. Available devices: {numDestinations}");
        }

        _destination = NativeMethods.MIDIGetDestination(_deviceIndex);
        if (_destination == IntPtr.Zero)
        {
            NativeMethods.MIDIPortDispose(_outputPort);
            NativeMethods.MIDIClientDispose(_client);
            _outputPort = IntPtr.Zero;
            _client = IntPtr.Zero;
            throw new InvalidOperationException($"Failed to get MIDI destination {_deviceIndex}");
        }

        _isOpen = true;
    }

    public void Close()
    {
        if (!_isOpen) return;

        AllNotesOff();

        if (_outputPort != IntPtr.Zero)
        {
            NativeMethods.MIDIPortDispose(_outputPort);
            _outputPort = IntPtr.Zero;
        }

        if (_client != IntPtr.Zero)
        {
            NativeMethods.MIDIClientDispose(_client);
            _client = IntPtr.Zero;
        }

        _destination = IntPtr.Zero;
        _isOpen = false;
    }

    public void SendShortMessage(byte status, byte data1, byte data2)
    {
        ThrowIfNotOpen();
        Span<byte> data = stackalloc byte[3] { status, data1, data2 };
        SendPacket(data);
    }

    public void SendShortMessage(byte status, byte data1)
    {
        ThrowIfNotOpen();
        Span<byte> data = stackalloc byte[2] { status, data1 };
        SendPacket(data);
    }

    public void SendSysEx(ReadOnlySpan<byte> data)
    {
        ThrowIfNotOpen();
        SendPacket(data);
    }

    public void AllNotesOff()
    {
        if (!_isOpen) return;

        // Send All Notes Off (CC 123) on all channels
        Span<byte> msg = stackalloc byte[3];
        msg[1] = 123;
        msg[2] = 0;
        for (int channel = 0; channel < 16; channel++)
        {
            msg[0] = (byte)(0xB0 | channel);
            SendPacket(msg);
        }
    }

    public void Reset()
    {
        if (!_isOpen) return;

        // Reset all controllers on all channels
        Span<byte> msg = stackalloc byte[3];
        msg[1] = 121; // Reset All Controllers (CC 121)
        msg[2] = 0;
        for (int channel = 0; channel < 16; channel++)
        {
            msg[0] = (byte)(0xB0 | channel);
            SendPacket(msg);
        }
    }

    private unsafe void SendPacket(ReadOnlySpan<byte> data)
    {
        // MIDIPacketList structure:
        // UInt32 numPackets
        // MIDIPacket[1] packet (variable size array)
        //
        // MIDIPacket structure:
        // MIDITimeStamp timeStamp (UInt64)
        // UInt16 length
        // Byte[256] data

        const int MaxPacketListSize = 512;
        byte* buffer = stackalloc byte[MaxPacketListSize];

        // Initialize packet list
        IntPtr packetList = (IntPtr)buffer;
        IntPtr currentPacket = NativeMethods.MIDIPacketListInit(packetList);

        // Add packet with timestamp 0 (send immediately)
        fixed (byte* dataPtr = data)
        {
            currentPacket = NativeMethods.MIDIPacketListAdd(
                packetList,
                MaxPacketListSize,
                currentPacket,
                0,  // timestamp 0 = now
                data.Length,
                dataPtr);
        }

        if (currentPacket == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to add MIDI packet to packet list");
        }

        // Send the packet
        int status = NativeMethods.MIDISend(_outputPort, _destination, packetList);
        if (status != 0)
        {
            throw new InvalidOperationException($"Failed to send MIDI packet. Error code: {status}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CoreMidiOutput));
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
}

/// <summary>
/// P/Invoke declarations for macOS Core MIDI framework.
/// </summary>
internal static unsafe class NativeMethods
{
    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // Core MIDI functions
    [DllImport(CoreMidi)]
    public static extern int MIDIClientCreate(IntPtr name, IntPtr notifyProc, IntPtr notifyRefCon, out IntPtr client);

    [DllImport(CoreMidi)]
    public static extern int MIDIClientDispose(IntPtr client);

    [DllImport(CoreMidi)]
    public static extern int MIDIOutputPortCreate(IntPtr client, IntPtr portName, out IntPtr port);

    [DllImport(CoreMidi)]
    public static extern int MIDIPortDispose(IntPtr port);

    [DllImport(CoreMidi)]
    public static extern int MIDIGetNumberOfDestinations();

    [DllImport(CoreMidi)]
    public static extern IntPtr MIDIGetDestination(int destIndex);

    [DllImport(CoreMidi)]
    public static extern int MIDISend(IntPtr port, IntPtr dest, IntPtr packetList);

    [DllImport(CoreMidi)]
    public static extern IntPtr MIDIPacketListInit(IntPtr packetList);

    [DllImport(CoreMidi)]
    public static extern IntPtr MIDIPacketListAdd(
        IntPtr packetList,
        int listSize,
        IntPtr currentPacket,
        ulong timeStamp,
        int dataLength,
        byte* data);

    [DllImport(CoreMidi)]
    public static extern int MIDIObjectGetStringProperty(IntPtr obj, IntPtr propertyID, out IntPtr str);

    // kMIDIPropertyName constant
    public static IntPtr kMIDIPropertyName => CreateCFString("name");

    // Core Foundation functions for string handling
    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, int encoding);

    [DllImport(CoreFoundation)]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation)]
    private static extern int CFStringGetLength(IntPtr theString);

    [DllImport(CoreFoundation)]
    private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, int bufferSize, int encoding);

    public static IntPtr CreateCFString(string str)
    {
        // kCFStringEncodingUTF8 = 0x08000100
        return CFStringCreateWithCString(IntPtr.Zero, str, 0x08000100);
    }

    public static string? CFStringGetCString(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero) return null;
        int length = CFStringGetLength(cfString);
        if (length == 0) return string.Empty;

        byte[] buffer = new byte[length * 4 + 1]; // UTF-8 can be up to 4 bytes per character
        if (CFStringGetCString(cfString, buffer, buffer.Length, 0x08000100))
        {
            int nullIndex = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, nullIndex >= 0 ? nullIndex : buffer.Length);
        }
        return null;
    }
}
