using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.Vst3.Vst3Abi;

namespace MidiSharp.Hosting.Vst3;

/// <summary>
/// A loaded VST3 effect. Drives the IComponent/IAudioProcessor/IEditController interfaces obtained from
/// one created object (single-component model): initialize → query the processor and controller → setup
/// processing → activate buses → planar <c>process</c> over a stereo bus (a 1-channel output is
/// duplicated). Parameters are 0..1 via the controller's <c>get/setParamNormalized</c> (block-granular,
/// the VST3 way for a host); a mono output is duplicated to the stereo bus.
/// </summary>
/// <remarks>
/// Covers the single-component effect path. Separate component/controller plugins (where the controller
/// is a distinct class), VST3 event lists (instrument MIDI), and IBStream state are follow-ups.
/// </remarks>
public sealed unsafe class Vst3Plugin : IHostedPlugin
{
    private readonly IntPtr _lib;
    private void* _component;
    private void* _processor;
    private void* _controller;

    private int _outputChannels = 2;
    private readonly List<uint> _paramIds = [];
    private readonly List<PluginParameter> _parameters = [];

    private float** _inCh;
    private float** _outCh;
    private AudioBusBuffers* _inBus;
    private AudioBusBuffers* _outBus;
    private ProcessData* _process;
    private bool _active;
    private bool _disposed;

    private ComponentVtbl* Comp => (ComponentVtbl*)*(void**)_component;
    private AudioProcessorVtbl* Proc => (AudioProcessorVtbl*)*(void**)_processor;
    private EditControllerVtbl* Ctrl => (EditControllerVtbl*)*(void**)_controller;

    internal Vst3Plugin(IntPtr lib, void* component, PluginDescriptor descriptor, AudioConfig config)
    {
        _lib = lib;
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

        void* proc = null, ctrl = null;
        fixed (byte* iidProc = IidAudioProcessor)
            if (!Ok(Comp->QueryInterface(_component, iidProc, &proc)) || proc == null)
                throw new NotSupportedException($"VST3 '{Descriptor.Name}' exposes no IAudioProcessor.");
        _processor = proc;
        fixed (byte* iidCtrl = IidEditController)
            if (Ok(Comp->QueryInterface(_component, iidCtrl, &ctrl))) _controller = ctrl;   // single-component

        if (_controller != null) BuildParameters();

        var setup = new ProcessSetup
        {
            ProcessMode = 0,
            SymbolicSampleSize = 0,   // kSample32
            MaxSamplesPerBlock = config.MaxBlockFrames,
            SampleRate = config.SampleRate,
        };
        Proc->SetupProcessing(_processor, &setup);

        // Output channel count from the main output bus.
        if (Comp->GetBusCount(_component, 0, 1) > 0)
        {
            BusInfo bus;
            if (Ok(Comp->GetBusInfo(_component, 0, 1, 0, &bus)) && bus.ChannelCount is 1 or 2)
                _outputChannels = bus.ChannelCount;
        }
        // Activate the audio buses, then go active and start processing.
        for (var i = 0; i < Comp->GetBusCount(_component, 0, 0); i++) Comp->ActivateBus(_component, 0, 0, i, 1);
        for (var i = 0; i < Comp->GetBusCount(_component, 0, 1); i++) Comp->ActivateBus(_component, 0, 1, i, 1);
        Comp->SetActive(_component, 1);
        Proc->SetProcessing(_processor, 1);

        _inCh = (float**)NativeMemory.AllocZeroed(2, (nuint)IntPtr.Size);
        _outCh = (float**)NativeMemory.AllocZeroed(2, (nuint)IntPtr.Size);
        _inBus = Alloc<AudioBusBuffers>();
        _outBus = Alloc<AudioBusBuffers>();
        _inBus->NumChannels = 2; _inBus->ChannelBuffers32 = _inCh;
        _outBus->NumChannels = _outputChannels; _outBus->ChannelBuffers32 = _outCh;
        _process = Alloc<ProcessData>();
        _process->NumInputs = 1; _process->NumOutputs = 1;
        _process->Inputs = _inBus; _process->Outputs = _outBus;
        _process->SymbolicSampleSize = 0; _process->ProcessMode = 0;

        _active = true;
    }

    public void Deactivate()
    {
        if (!_active) return;
        Proc->SetProcessing(_processor, 0);
        Comp->SetActive(_component, 0);
        Comp->Terminate(_component);
        if (_inCh != null) NativeMemory.Free(_inCh);
        if (_outCh != null) NativeMemory.Free(_outCh);
        Free(ref _inBus); Free(ref _outBus); Free(ref _process);
        _inCh = _outCh = null;
        _active = false;
    }

    public void Process(PlanarBuffers input, PlanarBuffers output, ReadOnlySpan<HostEvent> events)
    {
        if (!_active) return;

        // VST3 parameter changes are block-granular for a host driving setParamNormalized; MIDI/event
        // lists (instruments) are a follow-up, so non-param events are ignored here.
        foreach (var e in events)
            if (e.Kind == HostEventKind.Param && _controller != null && (uint)e.ParamIndex < (uint)_paramIds.Count)
                Ctrl->SetParamNormalized(_controller, _paramIds[e.ParamIndex], e.ParamValue);

        _inCh[0] = input.Channel(0); _inCh[1] = input.Channel(1);
        _outCh[0] = output.Channel(0); _outCh[1] = output.Channel(_outputChannels == 1 ? 0 : 1);
        _process->NumSamples = output.Frames;
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

    public byte[] SaveState() => [];                       // IBStream get/setState is a follow-up
    public void LoadState(ReadOnlySpan<byte> state) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
        if (_controller != null && _controller != _component) Release(_controller);
        if (_processor != null) Release(_processor);
        if (_component != null) Release(_component);
        _controller = _processor = _component = null;
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
