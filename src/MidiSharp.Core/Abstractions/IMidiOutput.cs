using System;

namespace MidiSharp.Abstractions;

/// <summary>
/// Interface for platform-specific MIDI output implementations.
/// </summary>
public interface IMidiOutput : IDisposable
{
    /// <summary>
    /// Opens the MIDI output device.
    /// </summary>
    void Open();

    /// <summary>
    /// Closes the MIDI output device.
    /// </summary>
    void Close();

    /// <summary>
    /// Returns true if the device is currently open.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Sends a short MIDI message (1-3 bytes).
    /// </summary>
    /// <param name="status">The status byte.</param>
    /// <param name="data1">First data byte.</param>
    /// <param name="data2">Second data byte.</param>
    void SendShortMessage(byte status, byte data1, byte data2);

    /// <summary>
    /// Sends a short MIDI message (1-2 bytes) for messages like Program Change.
    /// </summary>
    /// <param name="status">The status byte.</param>
    /// <param name="data1">First data byte.</param>
    void SendShortMessage(byte status, byte data1);

    /// <summary>
    /// Sends a System Exclusive message.
    /// </summary>
    /// <param name="data">The complete SysEx data including F0 and F7.</param>
    void SendSysEx(ReadOnlySpan<byte> data);

    /// <summary>
    /// Sends All Notes Off on all channels.
    /// </summary>
    void AllNotesOff();

    /// <summary>
    /// Resets the MIDI device to default state.
    /// </summary>
    void Reset();
}

/// <summary>
/// Information about a MIDI output device.
/// </summary>
public sealed class MidiOutputDeviceInfo
{
    public MidiOutputDeviceInfo(int index, string name, string? manufacturer = null)
    {
        Index = index;
        Name = name;
        Manufacturer = manufacturer;
    }

    public int Index { get; }
    public string Name { get; }
    public string? Manufacturer { get; }

    public override string ToString() => Name;
}

/// <summary>
/// Factory interface for creating platform-specific MIDI outputs.
/// </summary>
public interface IMidiOutputFactory
{
    /// <summary>
    /// Enumerates available MIDI output devices.
    /// </summary>
    MidiOutputDeviceInfo[] GetDevices();

    /// <summary>
    /// Creates a MIDI output for the specified device.
    /// </summary>
    /// <param name="deviceIndex">The device index (from GetDevices).</param>
    IMidiOutput Create(int deviceIndex);

    /// <summary>
    /// Creates a MIDI output for the default device.
    /// </summary>
    IMidiOutput CreateDefault();
}
