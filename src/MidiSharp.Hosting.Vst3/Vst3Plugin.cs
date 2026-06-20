using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.Vst3.Vst3Abi;

namespace MidiSharp.Hosting.Vst3;

/// <summary>
/// A loaded VST3 plugin (effect or instrument). Initializes the component, resolves the edit controller —
/// either from the same object (single-component) or, when the component exposes none, by creating the
/// distinct controller class the component names (separate component/controller model) and wiring the two
/// together — then sets up processing, activates buses, and runs the planar <c>process</c>. An instrument
/// (a plugin with an event input bus) is fed note-on/off through an <see cref="Vst3EventList"/>; component
/// and controller state round-trip through <see cref="Vst3BStream"/>.
/// </summary>
public sealed unsafe class Vst3Plugin : IHostedPlugin
{
    private readonly IntPtr _lib;
    private void* _factory;
    private void* _component;
    private void* _processor;
    private void* _controller;
    private bool _controllerSeparate;

    private int _outputChannels = 2;
    private int _audioInputs;
    private readonly List<uint> _paramIds = [];
    private readonly List<PluginParameter> _parameters = [];

    private float** _inCh;
    private float** _outCh;
    private AudioBusBuffers* _inBus;
    private AudioBusBuffers* _outBus;
    private ProcessData* _process;
    private Vst3EventList? _eventList;   // non-null when the plugin has an event input bus (an instrument)
    private bool _active;
    private bool _disposed;

    private ComponentVtbl* Comp => (ComponentVtbl*)*(void**)_component;
    private AudioProcessorVtbl* Proc => (AudioProcessorVtbl*)*(void**)_processor;
    private EditControllerVtbl* Ctrl => (EditControllerVtbl*)*(void**)_controller;

    internal Vst3Plugin(IntPtr lib, void* factory, void* component, PluginDescriptor descriptor, AudioConfig config)
    {
        _lib = lib;
        _factory = factory;
        _component = component;
        Descriptor = descriptor;
        Activate(config);
    }

    public PluginDescriptor Descriptor { get; }
    public bool IsInstrument => Descriptor.IsInstrument;
    public IReadOnlyList<PluginParameter> Parameters => _parameters;

    public void Activate(AudioConfig config)
    {
        if (_active) return;

        Comp->Initialize(_component, Vst3Host.HostApplication);

        void* proc = null;
        fixed (byte* iidProc = IidAudioProcessor)
            if (!Ok(Comp->QueryInterface(_component, iidProc, &proc)) || proc == null)
                throw new NotSupportedException($"VST3 '{Descriptor.Name}' exposes no IAudioProcessor.");
        _processor = proc;

        ResolveController();
        if (_controller != null) BuildParameters();

        var setup = new ProcessSetup
        {
            ProcessMode = 0,
            SymbolicSampleSize = 0,   // kSample32
            MaxSamplesPerBlock = config.MaxBlockFrames,
            SampleRate = config.SampleRate,
        };
        Proc->SetupProcessing(_processor, &setup);

        // Bus topology: audio inputs (0 for a pure instrument), the main audio output's channel count, and
        // whether there's an event input bus (the instrument signal).
        _audioInputs = Comp->GetBusCount(_component, 0, 0);
        if (Comp->GetBusCount(_component, 0, 1) > 0)
        {
            BusInfo bus;
            if (Ok(Comp->GetBusInfo(_component, 0, 1, 0, &bus)) && bus.ChannelCount is 1 or 2)
                _outputChannels = bus.ChannelCount;
        }
        var eventInputs = Comp->GetBusCount(_component, 1, 0);   // kEvent, kInput

        // Activate every bus on the main media types, then go active and start processing.
        for (var i = 0; i < _audioInputs; i++) Comp->ActivateBus(_component, 0, 0, i, 1);
        for (var i = 0; i < Comp->GetBusCount(_component, 0, 1); i++) Comp->ActivateBus(_component, 0, 1, i, 1);
        for (var i = 0; i < eventInputs; i++) Comp->ActivateBus(_component, 1, 0, i, 1);
        Comp->SetActive(_component, 1);
        Proc->SetProcessing(_processor, 1);

        _inCh = (float**)NativeMemory.AllocZeroed(2, (nuint)IntPtr.Size);
        _outCh = (float**)NativeMemory.AllocZeroed(2, (nuint)IntPtr.Size);
        _inBus = Alloc<AudioBusBuffers>();
        _outBus = Alloc<AudioBusBuffers>();
        _inBus->NumChannels = 2; _inBus->ChannelBuffers32 = _inCh;
        _outBus->NumChannels = _outputChannels; _outBus->ChannelBuffers32 = _outCh;
        _process = Alloc<ProcessData>();
        _process->NumInputs = _audioInputs > 0 ? 1 : 0;
        _process->NumOutputs = 1;
        _process->Inputs = _audioInputs > 0 ? _inBus : null;
        _process->Outputs = _outBus;
        _process->SymbolicSampleSize = 0; _process->ProcessMode = 0;

        if (eventInputs > 0) _eventList = new Vst3EventList();

        _active = true;
    }

    // Resolve the edit controller. Most plugins expose IEditController on the component object
    // (single-component). When that QI fails, the component names a separate controller class via
    // getControllerClassId — create it from the factory, initialize it with the host, connect the two via
    // IConnectionPoint, and seed it with the component's state so its parameters match.
    private void ResolveController()
    {
        void* ctrl = null;
        fixed (byte* iidCtrl = IidEditController)
            if (Ok(Comp->QueryInterface(_component, iidCtrl, &ctrl)) && ctrl != null)
            {
                _controller = ctrl;   // single-component
                return;
            }

        if (_factory == null) return;
        var cid = stackalloc byte[16];
        if (!Ok(Comp->GetControllerClassId(_component, cid))) return;

        var fac = (FactoryVtbl*)*(void**)_factory;
        void* created = null;
        fixed (byte* iidCtrl = IidEditController)
            if (!Ok(fac->CreateInstance(_factory, cid, iidCtrl, &created)) || created == null)
                return;   // controller class unavailable → run processor-only (no params/state UI)

        _controller = created;
        _controllerSeparate = true;
        Ctrl->Initialize(_controller, Vst3Host.HostApplication);
        ConnectComponentAndController();
        SyncControllerToComponentState();
    }

    private void ConnectComponentAndController()
    {
        void* compCp = null, ctrlCp = null;
        fixed (byte* iid = IidConnectionPoint)
        {
            Comp->QueryInterface(_component, iid, &compCp);
            Ctrl->QueryInterface(_controller, iid, &ctrlCp);
        }
        if (compCp != null && ctrlCp != null)
        {
            var a = (ConnectionPointVtbl*)*(void**)compCp;
            var b = (ConnectionPointVtbl*)*(void**)ctrlCp;
            a->Connect(compCp, ctrlCp);
            b->Connect(ctrlCp, compCp);
        }
        if (compCp != null) Release(compCp);
        if (ctrlCp != null) Release(ctrlCp);
    }

    // Pass the component's current state to the controller (setComponentState) so a separate controller's
    // parameter cache reflects the processor. Best-effort: a component that writes no state is fine.
    private void SyncControllerToComponentState()
    {
        using var stream = Vst3BStream.ForWrite();
        if (Ok(Comp->GetState(_component, stream.Pointer)))
        {
            using var read = Vst3BStream.ForRead(stream.ToArray());
            Ctrl->SetComponentState(_controller, read.Pointer);
        }
    }

    public void Deactivate()
    {
        if (!_active) return;
        Proc->SetProcessing(_processor, 0);
        Comp->SetActive(_component, 0);
        Comp->Terminate(_component);
        _eventList?.Dispose(); _eventList = null;
        if (_inCh != null) NativeMemory.Free(_inCh);
        if (_outCh != null) NativeMemory.Free(_outCh);
        Free(ref _inBus); Free(ref _outBus); Free(ref _process);
        _inCh = _outCh = null;
        _active = false;
    }

    public void Process(PlanarBuffers input, PlanarBuffers output, ReadOnlySpan<HostEvent> events)
    {
        if (!_active) return;

        // Parameter changes are block-granular for a host driving setParamNormalized; note events go to the
        // instrument's event list (sample-accurate via the event's sampleOffset).
        _eventList?.Clear();
        foreach (var e in events)
        {
            if (e.Kind == HostEventKind.Param)
            {
                if (_controller != null && (uint)e.ParamIndex < (uint)_paramIds.Count)
                    Ctrl->SetParamNormalized(_controller, _paramIds[e.ParamIndex], e.ParamValue);
            }
            else if (_eventList != null && e.Kind == HostEventKind.Midi)
            {
                var status = e.Status & 0xF0;
                var channel = e.Status & 0x0F;
                if (status == 0x90 && e.Data2 > 0) _eventList.AddNoteOn(e.SampleOffset, channel, e.Data1, e.Data2);
                else if (status == 0x80 || (status == 0x90 && e.Data2 == 0)) _eventList.AddNoteOff(e.SampleOffset, channel, e.Data1, e.Data2);
            }
        }

        _inCh[0] = input.Channel(0); _inCh[1] = input.Channel(1);
        _outCh[0] = output.Channel(0); _outCh[1] = output.Channel(_outputChannels == 1 ? 0 : 1);
        _process->NumSamples = output.Frames;
        _process->InputEvents = _eventList != null ? _eventList.Pointer : null;
        Proc->Process(_processor, _process);

        if (_outputChannels == 1)
            new ReadOnlySpan<float>(output.Channel(0), output.Frames).CopyTo(new Span<float>(output.Channel(1), output.Frames));
    }

    public double GetParameter(int index)
        => _controller != null && (uint)index < (uint)_parameters.Count ? Ctrl->GetParamNormalized(_controller, _paramIds[index]) : 0;

    public void SetParameter(int index, double normalized)
    {
        if (_controller != null && (uint)index < (uint)_parameters.Count)
            Ctrl->SetParamNormalized(_controller, _paramIds[index], Math.Clamp(normalized, 0, 1));
    }

    // State round-trips through the component's getState/setState (the authoritative processor state). When
    // the controller is a separate object it also gets the component state on load (setComponentState) so
    // its parameter cache follows, plus its own getState/setState for any controller-only UI state. The
    // blob is length-prefixed [compLen][comp][ctrlLen][ctrl] so either half may be empty.
    public byte[] SaveState()
    {
        using var comp = Vst3BStream.ForWrite();
        Comp->GetState(_component, comp.Pointer);
        var compBytes = comp.ToArray();

        var ctrlBytes = Array.Empty<byte>();
        if (_controllerSeparate)
        {
            using var ctrl = Vst3BStream.ForWrite();
            if (Ok(Ctrl->GetState(_controller, ctrl.Pointer))) ctrlBytes = ctrl.ToArray();
        }

        var blob = new byte[8 + compBytes.Length + ctrlBytes.Length];
        BitConverter.TryWriteBytes(blob.AsSpan(0), compBytes.Length);
        compBytes.CopyTo(blob.AsSpan(4));
        BitConverter.TryWriteBytes(blob.AsSpan(4 + compBytes.Length), ctrlBytes.Length);
        ctrlBytes.CopyTo(blob.AsSpan(8 + compBytes.Length));
        return blob;
    }

    public void LoadState(ReadOnlySpan<byte> state)
    {
        if (state.Length < 8) return;
        var compLen = BitConverter.ToInt32(state[..4]);
        if (compLen < 0 || 4 + compLen + 4 > state.Length) return;
        var compBytes = state.Slice(4, compLen);
        var ctrlLen = BitConverter.ToInt32(state.Slice(4 + compLen, 4));
        var ctrlBytes = ctrlLen > 0 && 8 + compLen + ctrlLen <= state.Length
            ? state.Slice(8 + compLen, ctrlLen) : ReadOnlySpan<byte>.Empty;

        if (compLen > 0)
        {
            using var comp = Vst3BStream.ForRead(compBytes);
            Comp->SetState(_component, comp.Pointer);
            if (_controllerSeparate)
            {
                comp.Rewind();
                Ctrl->SetComponentState(_controller, comp.Pointer);
            }
        }
        if (_controllerSeparate && !ctrlBytes.IsEmpty)
        {
            using var ctrl = Vst3BStream.ForRead(ctrlBytes);
            Ctrl->SetState(_controller, ctrl.Pointer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
        if (_controllerSeparate && _controller != null) { Ctrl->Terminate(_controller); }
        if (_controller != null && _controller != _component) Release(_controller);
        if (_processor != null) Release(_processor);
        if (_component != null) Release(_component);
        _controller = _processor = _component = _factory = null;
        if (_lib != IntPtr.Zero) NativeLibrary.Free(_lib);
    }

    private void BuildParameters()
    {
        var count = Ctrl->GetParameterCount(_controller);
        for (var i = 0; i < count; i++)
        {
            ParameterInfo info;
            if (!Ok(Ctrl->GetParameterInfo(_controller, i, &info))) continue;
            var name = Utf16(info.Title, 128);
            _parameters.Add(new PluginParameter(_paramIds.Count, string.IsNullOrEmpty(name) ? $"Param {i + 1}" : name,
                label: "", minValue: 0, maxValue: 1, defaultValue: info.DefaultNormalizedValue));
            _paramIds.Add(info.Id);
        }
    }

    private static T* Alloc<T>() where T : unmanaged => (T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
    private static void Free<T>(ref T* p) where T : unmanaged { if (p != null) { NativeMemory.Free(p); p = null; } }

    // Call FUnknown::release (vtable slot 2) on any interface pointer.
    private static uint Release(void* obj)
    {
        var vtbl = *(IntPtr**)obj;
        return ((delegate* unmanaged[Cdecl]<void*, uint>)vtbl[2])(obj);
    }
}
