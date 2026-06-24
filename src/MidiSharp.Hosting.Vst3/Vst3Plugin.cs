using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
public sealed unsafe class Vst3Plugin : IHostedPlugin, IPluginGui
{
    private readonly IntPtr _lib;
    private void* _factory;
    private void* _component;
    private void* _processor;
    private void* _controller;
    private void* _view;            // IPlugView, created lazily from the controller (on the editor UI thread)
    private Vst3PlugFrame? _frame;  // host frame + IRunLoop for the open editor
    private volatile IEditorRunLoop? _editorLoop;   // non-null while an editor is open → params marshal to the UI thread
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
    private PlugViewVtbl* View => (PlugViewVtbl*)*(void**)_view;

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
        int eventInputs = Comp->GetBusCount(_component, 1, 0);   // kEvent, kInput

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
                Ctrl->SetComponentHandler(_controller, Vst3Host.ComponentHandler);   // the host MUST hand the controller a handler before createView
                return;
            }

        if (_factory == null) return;
        byte* cid = stackalloc byte[16];
        if (!Ok(Comp->GetControllerClassId(_component, cid))) return;

        var fac = (FactoryVtbl*)*(void**)_factory;
        void* created = null;
        fixed (byte* iidCtrl = IidEditController)
            if (!Ok(fac->CreateInstance(_factory, cid, iidCtrl, &created)) || created == null)
                return;   // controller class unavailable → run processor-only (no params/state UI)

        _controller = created;
        _controllerSeparate = true;
        Ctrl->Initialize(_controller, Vst3Host.HostApplication);
        Ctrl->SetComponentHandler(_controller, Vst3Host.ComponentHandler);   // initialize → setComponentHandler → connect, before createView
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
        using Vst3BStream stream = Vst3BStream.ForWrite();
        if (Ok(Comp->GetState(_component, stream.Pointer)))
        {
            using Vst3BStream read = Vst3BStream.ForRead(stream.ToArray());
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
        foreach (HostEvent e in events)
        {
            if (e.Kind == HostEventKind.Param)
            {
                if (_controller != null && (uint)e.ParamIndex < (uint)_paramIds.Count)
                    Ctrl->SetParamNormalized(_controller, _paramIds[e.ParamIndex], e.ParamValue);
            }
            else if (_eventList != null && e.Kind == HostEventKind.Midi)
            {
                int status = e.Status & 0xF0;
                int channel = e.Status & 0x0F;
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
    {
        if (_controller == null || (uint)index >= (uint)_parameters.Count) return 0;
        uint id = _paramIds[index];
        return OnUiThread(() => Ctrl->GetParamNormalized(_controller, id));
    }

    public void SetParameter(int index, double normalized)
    {
        if (_controller == null || (uint)index >= (uint)_parameters.Count) return;
        uint id = _paramIds[index];
        double v = Math.Clamp(normalized, 0, 1);
        OnUiThread(() => { Ctrl->SetParamNormalized(_controller, id, v); return 0.0; });
    }

    // VST3 controller calls (get/setParamNormalized) are main-thread-only. While an editor is open the UI
    // thread owns the controller (it drives the view), so marshal param access onto it; otherwise call
    // directly. Bounded wait so a wedged editor can't hang a param request.
    private double OnUiThread(Func<double> fn)
    {
        IEditorRunLoop? loop = _editorLoop;
        if (loop == null) return fn();
        double result = 0;
        using var done = new System.Threading.ManualResetEventSlim(false);
        loop.Post(() => { try { result = fn(); } finally { done.Set(); } });
        done.Wait(800);
        return result;
    }

    // State round-trips through the component's getState/setState (the authoritative processor state). When
    // the controller is a separate object it also gets the component state on load (setComponentState) so
    // its parameter cache follows, plus its own getState/setState for any controller-only UI state. The
    // blob is length-prefixed [compLen][comp][ctrlLen][ctrl] so either half may be empty.
    public byte[] SaveState()
    {
        using Vst3BStream comp = Vst3BStream.ForWrite();
        Comp->GetState(_component, comp.Pointer);
        byte[] compBytes = comp.ToArray();

        byte[] ctrlBytes = Array.Empty<byte>();
        if (_controllerSeparate)
        {
            using Vst3BStream ctrl = Vst3BStream.ForWrite();
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
        ReadOnlySpan<byte> compBytes = state.Slice(4, compLen);
        var ctrlLen = BitConverter.ToInt32(state.Slice(4 + compLen, 4));
        ReadOnlySpan<byte> ctrlBytes = ctrlLen > 0 && 8 + compLen + ctrlLen <= state.Length
            ? state.Slice(8 + compLen, ctrlLen) : ReadOnlySpan<byte>.Empty;

        if (compLen > 0)
        {
            using Vst3BStream comp = Vst3BStream.ForRead(compBytes);
            Comp->SetState(_component, comp.Pointer);
            if (_controllerSeparate)
            {
                comp.Rewind();
                Ctrl->SetComponentState(_controller, comp.Pointer);
            }
        }
        if (_controllerSeparate && !ctrlBytes.IsEmpty)
        {
            using Vst3BStream ctrl = Vst3BStream.ForRead(ctrlBytes);
            Ctrl->SetState(_controller, ctrl.Pointer);
        }
    }

    // ── Editor (IPlugView) ──────────────────────────────────────────────────────────────────────────
    // A controller (almost always) means an editor; the view is created lazily in Create(), on the editor
    // thread — so HasEditor stays cheap and thread-safe (the worker queries it on the load thread).
    public IPluginGui? Gui => _controller != null ? this : null;

    // Create the editor view on first need (cached). The controller owns it; we release on destroy/dispose.
    // Must be called on the editor UI thread (GUI toolkits are thread-affine).
    private void EnsureView()
    {
        if (_view != null || _controller == null) return;
        Span<byte> name = stackalloc byte[8];
        AsciiZ("editor", name);
        fixed (byte* p = name) _view = Ctrl->CreateView(_controller, p);
    }

    bool IPluginGui.HasEditor => _controller != null;

    void IPluginGui.BindRunLoop(IEditorRunLoop? runLoop)
    {
        _frame?.Dispose();
        _frame = runLoop != null ? new Vst3PlugFrame(runLoop) : null;
        _editorLoop = runLoop;
    }

    // Map our window-API name to the VST3 platform-type string the view expects: X11 on Linux, HWND on
    // Windows. Cocoa ("NSView") slots in when a macOS backend lands. Null => unsupported here.
    private static string? PlatformTypeFor(string windowApi) => windowApi switch
    {
        "x11" => PlatformTypeX11,
        "win32" => PlatformTypeHwnd,
        "cocoa" => PlatformTypeNsView,
        _ => null,
    };

    bool IPluginGui.IsApiSupported(string windowApi, bool floating)
    {
        EnsureView();
        string? platformType = PlatformTypeFor(windowApi);
        if (_view == null || platformType == null) return false;
        Span<byte> t = stackalloc byte[20];
        AsciiZ(platformType, t);
        fixed (byte* p = t) return Ok(View->IsPlatformTypeSupported(_view, p));
    }

    bool IPluginGui.Create(string windowApi, bool floating)
    {
        EnsureView();
        if (_view == null) return false;
        // The frame carries our Linux IRunLoop; the view queries it off the frame to register its fds/timers.
        View->SetFrame(_view, _frame != null ? _frame.Frame : Vst3Host.PlugFrame);
        return true;
    }

    bool IPluginGui.SetScale(double scale) => true;   // VST3 content scaling is a separate interface; skip

    bool IPluginGui.TryGetSize(out int width, out int height)
    {
        width = height = 0;
        if (_view == null) return false;
        ViewRect r;
        if (!Ok(View->GetSize(_view, &r))) return false;
        width = r.Right - r.Left; height = r.Bottom - r.Top;
        return width > 0 && height > 0;
    }

    bool IPluginGui.SetParent(string windowApi, ulong windowHandle)
    {
        string? platformType = PlatformTypeFor(windowApi);
        if (_view == null || platformType == null) return false;
        Span<byte> t = stackalloc byte[20];
        AsciiZ(platformType, t);
        fixed (byte* p = t) return Ok(View->Attached(_view, (void*)(nuint)windowHandle, p));
    }

    bool IPluginGui.Show() => true;    // VST3 has no show; the view is visible once attached + window mapped
    bool IPluginGui.Hide() => true;

    void IPluginGui.Destroy()
    {
        if (_view != null) { View->Removed(_view); Release(_view); _view = null; }
        _frame?.Dispose(); _frame = null;
    }

    private static void AsciiZ(string s, Span<byte> dst)
    {
        int n = Math.Min(s.Length, dst.Length - 1);
        for (var i = 0; i < n; i++) dst[i] = (byte)s[i];
        dst[n] = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_view != null) { View->Removed(_view); Release(_view); _view = null; }
        _frame?.Dispose(); _frame = null;
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
        int count = Ctrl->GetParameterCount(_controller);
        for (var i = 0; i < count; i++)
        {
            ParameterInfo info;
            if (!Ok(Ctrl->GetParameterInfo(_controller, i, &info))) continue;
            string name = Utf16(info.Title, 128);
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
        IntPtr* vtbl = *(IntPtr**)obj;
        return ((delegate* unmanaged[Cdecl]<void*, uint>)vtbl[2])(obj);
    }
}
