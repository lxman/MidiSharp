using System;
using MidiSharp.Abstractions;

namespace MidiSharp.Synth;

/// <summary>
/// Adapter that wraps a Synthesizer to implement IMidiOutput,
/// allowing it to be used with MidiPlayer for MIDI file playback.
/// </summary>
public sealed class SynthesizerMidiOutput : IMidiOutput
{
    private readonly Synthesizer _synthesizer;
    private bool _isOpen;
    private bool _disposed;

    /// <summary>
    /// Creates a new synthesizer MIDI output adapter.
    /// </summary>
    /// <param name="synthesizer">The synthesizer to wrap.</param>
    public SynthesizerMidiOutput(Synthesizer synthesizer)
    {
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
    }

    /// <inheritdoc />
    public bool IsOpen => _isOpen;

    /// <inheritdoc />
    public void Open()
    {
        ThrowIfDisposed();
        _isOpen = true;
    }

    /// <inheritdoc />
    public void Close()
    {
        if (!_isOpen) return;
        _isOpen = false;
        AllNotesOff();
    }

    /// <inheritdoc />
    public void SendShortMessage(byte status, byte data1, byte data2)
    {
        if (!_isOpen) return;

        var messageType = status & 0xF0;
        var channel = status & 0x0F;

        switch (messageType)
        {
            case 0x80: // Note Off
                _synthesizer.NoteOff(channel, data1);
                break;

            case 0x90: // Note On
                if (data2 == 0)
                    _synthesizer.NoteOff(channel, data1);
                else
                    _synthesizer.NoteOn(channel, data1, data2);
                break;

            case 0xA0: // Polyphonic Aftertouch
                _synthesizer.PolyPressure(channel, data1, data2);
                break;

            case 0xB0: // Control Change
                _synthesizer.ControlChange(channel, data1, data2);
                break;

            case 0xC0: // Program Change (uses single-byte overload typically)
                _synthesizer.ProgramChange(channel, data1);
                break;

            case 0xD0: // Channel Aftertouch
                _synthesizer.ChannelPressure(channel, data1);
                break;

            case 0xE0: // Pitch Bend
                var bendValue = (data2 << 7) | data1;
                _synthesizer.PitchBend(channel, bendValue);
                break;
        }
    }

    /// <inheritdoc />
    public void SendShortMessage(byte status, byte data1)
    {
        if (!_isOpen) return;

        var messageType = status & 0xF0;
        var channel = status & 0x0F;

        switch (messageType)
        {
            case 0xC0: // Program Change
                _synthesizer.ProgramChange(channel, data1);
                break;

            case 0xD0: // Channel Aftertouch
                _synthesizer.ChannelPressure(channel, data1);
                break;
        }
    }

    /// <inheritdoc />
    public void SendSysEx(ReadOnlySpan<byte> data)
    {
        if (!_isOpen) return;
        _synthesizer.SysEx(data);
    }

    /// <inheritdoc />
    public void AllNotesOff()
    {
        // Send All Notes Off (CC 123) to all channels
        for (var channel = 0; channel < 16; channel++)
        {
            _synthesizer.ControlChange(channel, 123, 0);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        AllNotesOff();

        // Reset all controllers on all channels
        for (var channel = 0; channel < 16; channel++)
        {
            // Reset all controllers (CC 121)
            _synthesizer.ControlChange(channel, 121, 0);

            // Reset to default program
            _synthesizer.ProgramChange(channel, 0);

            // Reset pitch bend to center
            _synthesizer.PitchBend(channel, 8192);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SynthesizerMidiOutput));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }
}
