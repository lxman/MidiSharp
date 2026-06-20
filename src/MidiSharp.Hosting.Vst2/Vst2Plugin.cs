using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.Vst2.Vst2Abi;

namespace MidiSharp.Hosting.Vst2;

/// <summary>
/// A loaded VST2 plugin. Maps the VST2 <c>AEffect</c> onto <see cref="IHostedPlugin"/>: planar
/// <c>processReplacing</c> over a stereo bus (a 1-channel output is duplicated to stereo); parameters are
/// plain 0..1 floats via <c>setParameter</c>/<c>getParameter</c>; MIDI events are delivered with
/// per-event <c>deltaFrames</c> via the <c>effProcessEvents</c> opcode before each process call; opaque
/// state via <c>effGetChunk</c>/<c>effSetChunk</c> when the plugin advertises program chunks.
/// </summary>
/// <remarks>
/// VST2 parameter changes apply at block granularity (the ABI has no per-sample parameter events), so a
/// <see cref="HostEventKind.Param"/> event is applied immediately via <c>setParameter</c>; MIDI events are
/// sample-accurate through <c>deltaFrames</c>. Only mono/stereo main I/O is supported.
/// </remarks>
public sealed unsafe class Vst2Plugin : IHostedPlugin, IPluginGui
{
    private readonly IntPtr _lib;
    private AEffect* _eff;
    private bool _editorOpen;

    private int _numIn, _numOut;
    private readonly List<PluginParameter> _parameters = [];

    private float** _inPtrs;
    private float** _outPtrs;

    // VST2 event scratch: a VstMidiEvent array plus the VstEvents header/pointer block.
    private VstMidiEvent* _midiEvents;
    private byte* _vstEvents;
    private int _evCapacity;

    private bool _active;
    private bool _disposed;

    internal Vst2Plugin(IntPtr lib, AEffect* eff, PluginDescriptor descriptor, AudioConfig config)
    {
        _lib = lib;
        _eff = eff;
        Descriptor = descriptor;
        Activate(config);
    }

    public PluginDescriptor Descriptor { get; }
    public bool IsInstrument => (_eff->Flags & FlagsIsSynth) != 0;
    public IReadOnlyList<PluginParameter> Parameters => _parameters;

    public void Activate(AudioConfig config)
    {
        if (_active) return;
        _numIn = _eff->NumInputs;
        _numOut = _eff->NumOutputs;
        if (_numOut is < 1 or > 2)
            throw new NotSupportedException($"VST2 '{Descriptor.Name}' has {_numOut} outputs; only mono/stereo is supported.");

        BuildParameters();

        _eff->Dispatcher(_eff, EffSetSampleRate, 0, IntPtr.Zero, null, config.SampleRate);
        _eff->Dispatcher(_eff, EffSetBlockSize, 0, (IntPtr)config.MaxBlockFrames, null, 0);
        _eff->Dispatcher(_eff, EffMainsChanged, 0, (IntPtr)1, null, 0);   // resume

        _inPtrs = (float**)NativeMemory.AllocZeroed((nuint)Math.Max(1, _numIn), (nuint)IntPtr.Size);
        _outPtrs = (float**)NativeMemory.AllocZeroed((nuint)Math.Max(1, _numOut), (nuint)IntPtr.Size);

        _evCapacity = 512;
        _midiEvents = (VstMidiEvent*)NativeMemory.AllocZeroed((nuint)_evCapacity, (nuint)sizeof(VstMidiEvent));
        _vstEvents = (byte*)NativeMemory.AllocZeroed((nuint)(VstEventsHeaderBytes + _evCapacity * IntPtr.Size), 1);

        _active = true;
    }

    public void Deactivate()
    {
        if (!_active) return;
        _eff->Dispatcher(_eff, EffMainsChanged, 0, IntPtr.Zero, null, 0);   // suspend
        if (_inPtrs != null) { NativeMemory.Free(_inPtrs); _inPtrs = null; }
        if (_outPtrs != null) { NativeMemory.Free(_outPtrs); _outPtrs = null; }
        if (_midiEvents != null) { NativeMemory.Free(_midiEvents); _midiEvents = null; }
        if (_vstEvents != null) { NativeMemory.Free(_vstEvents); _vstEvents = null; }
        _active = false;
    }

    public void Process(PlanarBuffers input, PlanarBuffers output, ReadOnlySpan<HostEvent> events)
    {
        if (!_active) return;

        // Parameter events apply at block granularity; MIDI events go through effProcessEvents with
        // deltaFrames (sample-accurate). Build the VstEvents block from this call's MIDI events.
        var n = 0;
        foreach (var e in events)
        {
            if (e.Kind == HostEventKind.Param)
            {
                if ((uint)e.ParamIndex < (uint)_parameters.Count) _eff->SetParameter(_eff, e.ParamIndex, (float)e.ParamValue);
            }
            else if (n < _evCapacity)
            {
                ref var m = ref _midiEvents[n];
                m.Type = VstMidiType;
                m.ByteSize = sizeof(VstMidiEvent);
                m.DeltaFrames = e.SampleOffset;
                m.Flags = 0; m.NoteLength = 0; m.NoteOffset = 0;
                m.Midi0 = e.Status; m.Midi1 = e.Data1; m.Midi2 = e.Data2; m.Midi3 = 0;
                m.Detune = 0; m.NoteOffVelocity = 0; m.Reserved1 = 0; m.Reserved2 = 0;
                *(IntPtr*)(_vstEvents + VstEventsHeaderBytes + n * IntPtr.Size) = (IntPtr)(&_midiEvents[n]);
                n++;
            }
        }
        if (n > 0)
        {
            *(int*)_vstEvents = n;                          // numEvents
            *(IntPtr*)(_vstEvents + 8) = IntPtr.Zero;       // reserved
            _eff->Dispatcher(_eff, EffProcessEvents, 0, IntPtr.Zero, _vstEvents, 0);
        }

        for (var c = 0; c < _numIn; c++) _inPtrs[c] = input.Channel(Math.Min(c, 1));
        for (var c = 0; c < _numOut; c++) _outPtrs[c] = output.Channel(c);
        _eff->ProcessReplacing(_eff, _inPtrs, _outPtrs, output.Frames);

        if (_numOut == 1)   // mono → fill the stereo bus
            new ReadOnlySpan<float>(output.Channel(0), output.Frames).CopyTo(new Span<float>(output.Channel(1), output.Frames));
    }

    public double GetParameter(int index)
        => (uint)index < (uint)_parameters.Count ? _eff->GetParameter(_eff, index) : 0;

    public void SetParameter(int index, double normalized)
    {
        if ((uint)index < (uint)_parameters.Count) _eff->SetParameter(_eff, index, (float)Math.Clamp(normalized, 0, 1));
    }

    public byte[] SaveState()
    {
        if ((_eff->Flags & FlagsProgramChunks) == 0) return [];
        IntPtr data;
        var size = (long)_eff->Dispatcher(_eff, EffGetChunk, 0, IntPtr.Zero, &data, 0);
        if (size <= 0 || data == IntPtr.Zero) return [];
        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, (int)size);
        return bytes;
    }

    public void LoadState(ReadOnlySpan<byte> state)
    {
        if (state.IsEmpty || (_eff->Flags & FlagsProgramChunks) == 0) return;
        fixed (byte* p = state)
            _eff->Dispatcher(_eff, EffSetChunk, 0, (IntPtr)state.Length, p, 0);
    }

    // ── Editor (effEditOpen) ────────────────────────────────────────────────────────────────────────
    public IPluginGui? Gui => (_eff->Flags & FlagsHasEditor) != 0 ? this : null;

    bool IPluginGui.HasEditor => (_eff->Flags & FlagsHasEditor) != 0;

    bool IPluginGui.IsApiSupported(string windowApi, bool floating)
        => (_eff->Flags & FlagsHasEditor) != 0 && windowApi == "x11";   // VST2 on Linux embeds via X11

    bool IPluginGui.Create(string windowApi, bool floating) => (_eff->Flags & FlagsHasEditor) != 0;
    bool IPluginGui.SetScale(double scale) => true;

    bool IPluginGui.TryGetSize(out int width, out int height)
    {
        width = height = 0;
        ERect* rect = null;
        _eff->Dispatcher(_eff, EffEditGetRect, 0, IntPtr.Zero, &rect, 0);   // plugin writes an ERect* here
        if (rect == null) return false;
        width = rect->Right - rect->Left; height = rect->Bottom - rect->Top;
        return width > 0 && height > 0;
    }

    bool IPluginGui.SetParent(string windowApi, ulong windowHandle)
    {
        if ((_eff->Flags & FlagsHasEditor) == 0 || windowApi != "x11") return false;
        _eff->Dispatcher(_eff, EffEditOpen, 0, IntPtr.Zero, (void*)(nuint)windowHandle, 0);
        _editorOpen = true;
        return true;
    }

    bool IPluginGui.Show() => true;    // VST2 has no separate show; the editor draws once opened + mapped
    bool IPluginGui.Hide() => true;

    // VST2 editors repaint via periodic effEditIdle on the UI thread (the editor host ticks this ~30 ms).
    void IPluginGui.Idle()
    {
        if (_editorOpen && _eff != null) _eff->Dispatcher(_eff, EffEditIdle, 0, IntPtr.Zero, null, 0);
    }

    void IPluginGui.Destroy()
    {
        if (_editorOpen) { _eff->Dispatcher(_eff, EffEditClose, 0, IntPtr.Zero, null, 0); _editorOpen = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_editorOpen && _eff != null) { _eff->Dispatcher(_eff, EffEditClose, 0, IntPtr.Zero, null, 0); _editorOpen = false; }
        Deactivate();
        if (_eff != null) { _eff->Dispatcher(_eff, EffClose, 0, IntPtr.Zero, null, 0); _eff = null; }
        if (_lib != IntPtr.Zero) NativeLibrary.Free(_lib);
    }

    private void BuildParameters()
    {
        _parameters.Clear();
        for (var i = 0; i < _eff->NumParams; i++)
        {
            var name = Vst2Format.StringOp(_eff, EffGetParamName, i, 64);
            // VST2 parameters are already normalized 0..1; min/max are the identity range.
            _parameters.Add(new PluginParameter(i, string.IsNullOrEmpty(name) ? $"Param {i + 1}" : name,
                label: "", minValue: 0, maxValue: 1, defaultValue: _eff->GetParameter(_eff, i)));
        }
    }
}
