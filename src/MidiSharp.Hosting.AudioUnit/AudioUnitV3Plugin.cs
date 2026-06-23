using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MidiSharp.Hosting.AudioUnit.AudioUnitAbi;

namespace MidiSharp.Hosting.AudioUnit;

/// <summary>
/// A loaded AU hosted through the modern <c>AUAudioUnit</c> front-end (AU v3), mapped onto
/// <see cref="IHostedPlugin"/>. Unlike <see cref="AudioUnitPlugin"/> (the v2 C API), this drives audio with the
/// unit's realtime <c>renderBlock</c> — necessary because an out-of-process v3 AU exposes no v2 instance, and we
/// need the one <c>AUAudioUnit</c> object to also vend the plugin's custom editor (Plan B). Audio is
/// deinterleaved float32 (a standard <c>AVAudioFormat</c>), so each channel maps straight onto a
/// <see cref="PlanarBuffers"/> channel.
/// </summary>
/// <remarks>
/// Like AU, the unit pulls its input: <see cref="Process"/> stashes the block's input channels and passes a
/// constructed pull-input block that serves them. Parameters/state (Task 2), MIDI (Task 3) and the editor
/// (Task 4) build on this audio core.
/// </remarks>
public sealed unsafe class AudioUnitV3Plugin : IHostedPlugin, IPluginGui
{
    private IntPtr _au;                       // the AUAudioUnit (retained)
    private IntPtr _renderBlock;              // its cached realtime renderBlock (retained)
    private IntPtr _renderInvoke;             // renderBlock->invoke
    private IntPtr _pullBlock;                // our pull-input block (effects)
    private IntPtr _midiBlock, _midiInvoke;   // the AU's scheduleMIDIEventBlock (instruments)
    private IntPtr _viewController, _view;    // the custom editor's NSViewController + its NSView (while open)
    private readonly List<PluginParameter> _parameters = [];
    private readonly List<IntPtr> _paramObjs = [];   // parallel to _parameters: the retained AUParameter per index

    private float* _inCh0, _inCh1;            // this block's input channels (read by the pull block)
    private int _inFrames;
    private long _steady;
    private bool _active, _disposed;

    // The pull-input block has no refCon, so it finds its plugin via this thread-local set around each render.
    // renderBlock pulls input synchronously on the calling thread, so a thread-static is exact and lock-free.
    [ThreadStatic] private static AudioUnitV3Plugin? s_rendering;

    // renderBlock signature: OSStatus (flags*, AudioTimeStamp*, frameCount, NSInteger outBus, AudioBufferList*, pullInputBlock).
    private delegate* unmanaged[Cdecl]<IntPtr, uint*, AudioTimeStamp*, uint, nint, void*, IntPtr, int> Render
        => (delegate* unmanaged[Cdecl]<IntPtr, uint*, AudioTimeStamp*, uint, nint, void*, IntPtr, int>)_renderInvoke;

    internal AudioUnitV3Plugin(IntPtr au, PluginDescriptor descriptor, AudioConfig config)
    {
        _au = au;
        Descriptor = descriptor;
        Activate(config);
    }

    public PluginDescriptor Descriptor { get; }
    public bool IsInstrument => Descriptor.IsInstrument;
    public IReadOnlyList<PluginParameter> Parameters => _parameters;   // populated in Task 2

    public void Activate(AudioConfig config)
    {
        if (_active) return;

        IntPtr fmt = AuV3.StandardFormat(config.SampleRate, 2);
        if (fmt == IntPtr.Zero) throw new InvalidOperationException($"AVAudioFormat failed for '{Descriptor.Name}'.");
        try
        {
            if (!AuV3.SetBusFormat(_au, input: false, fmt))
                throw new InvalidOperationException($"Output bus rejected the stream format for '{Descriptor.Name}'.");
            if (!IsInstrument) AuV3.SetBusFormat(_au, input: true, fmt);   // effects pull an input bus
        }
        finally { AuAppKit.Release(fmt); }   // the busses retain it

        AuV3.SetMaxFrames(_au, config.MaxBlockFrames);
        if (!AuV3.AllocateRenderResources(_au))
            throw new InvalidOperationException($"allocateRenderResources failed for '{Descriptor.Name}'.");

        _renderBlock = AuV3.RenderBlock(_au);
        if (_renderBlock == IntPtr.Zero) throw new InvalidOperationException($"'{Descriptor.Name}' vended no renderBlock.");
        _renderInvoke = AuV3.InvokeOf(_renderBlock);
        if (!IsInstrument)
            _pullBlock = AuBlocks.MakeGlobalBlock((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint*, AudioTimeStamp*, uint, nint, void*, int>)&PullInput);
        else
        {
            _midiBlock = AuV3.ScheduleMidiBlock(_au);   // nil for effects; present for music devices
            if (_midiBlock != IntPtr.Zero) _midiInvoke = AuV3.InvokeOf(_midiBlock);
        }
        BuildParameters();
        _active = true;
    }

    // Flatten the unit's AUParameterTree into normalized PluginParameters (+ the retained AUParameter per index).
    // AUParameter values are raw, in [minValue, maxValue]; PluginParameter normalizes to 0..1 for the host.
    private void BuildParameters()
    {
        if (_parameters.Count > 0) return;   // built once; survives a Deactivate/Activate cycle
        IntPtr all = AuV3.AllParameters(AuV3.ParameterTree(_au));
        int count = AuV3.ArrayCount(all);
        for (int i = 0; i < count; i++)
        {
            IntPtr p = AuV3.ArrayAt(all, i);
            if (p == IntPtr.Zero) continue;
            AuAppKit.Retain(p);                                   // keep it past the autoreleased array
            uint unit = AuV3.ParamUnit(p);
            bool stepped = unit is ParamUnitIndexed or ParamUnitBoolean;
            _parameters.Add(new PluginParameter(_paramObjs.Count, AuV3.ParamDisplayName(p), label: "",
                AuV3.ParamMin(p), AuV3.ParamMax(p), AuV3.ParamValue(p), isStepped: stepped));
            _paramObjs.Add(p);
        }
    }

    public void Deactivate()
    {
        if (!_active) return;
        AuV3.DeallocateRenderResources(_au);
        _active = false;
    }

    public void Process(PlanarBuffers input, PlanarBuffers output, ReadOnlySpan<HostEvent> events)
    {
        if (!_active) return;

        // Parameter automation (block-coarse: AUParameter.value, not sample-accurate) + MIDI for instruments
        // (scheduled at "immediate + buffer offset", consumed by the renderBlock that follows).
        byte* midi = stackalloc byte[3];
        foreach (HostEvent e in events)
        {
            if (e.Kind == HostEventKind.Param && (uint)e.ParamIndex < (uint)_parameters.Count)
                AuV3.SetParamValue(_paramObjs[e.ParamIndex], (float)_parameters[e.ParamIndex].Denormalize(e.ParamValue));
            else if (e.Kind == HostEventKind.Midi && _midiBlock != IntPtr.Zero)
            {
                midi[0] = e.Status; midi[1] = e.Data1; midi[2] = e.Data2;
                nint length = (e.Status & 0xF0) is 0xC0 or 0xD0 ? 2 : 3;   // program-change / channel-pressure are 2 bytes
                ((delegate* unmanaged[Cdecl]<IntPtr, long, byte, nint, byte*, void>)_midiInvoke)(
                    _midiBlock, AuV3.MidiEventTimeImmediate + e.SampleOffset, 0, length, midi);
            }
        }

        _inFrames = input.Frames;
        _inCh0 = input.ChannelCount > 0 ? input.Channel(0) : null;
        _inCh1 = input.ChannelCount > 1 ? input.Channel(1) : _inCh0;

        var frames = (uint)output.Frames;
        var abl = new StereoBufferList
        {
            NumberBuffers = 2,
            Buffer0 = new AudioBuffer { NumberChannels = 1, DataByteSize = frames * 4, Data = output.Channel(0) },
            Buffer1 = new AudioBuffer { NumberChannels = 1, DataByteSize = frames * 4, Data = output.Channel(1) },
        };
        var ts = new AudioTimeStamp { SampleTime = _steady, Flags = TimeStampSampleTimeValid };
        uint flags = 0;

        s_rendering = this;
        try { Render(_renderBlock, &flags, &ts, frames, 0, &abl, _pullBlock); }
        finally { s_rendering = null; }
        _steady += frames;
    }

    // The "pull" input source: serve this block's stashed input into the buffers the AU presents.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int PullInput(IntPtr block, uint* ioActionFlags, AudioTimeStamp* ts, uint frames, nint inBus, void* ioData)
    {
        AudioUnitV3Plugin? self = s_rendering;
        if (self == null) return 0;
        var abl = (StereoBufferList*)ioData;
        Serve(&abl->Buffer0, self._inCh0, self._inFrames, frames);
        if (abl->NumberBuffers > 1) Serve(&abl->Buffer1, self._inCh1, self._inFrames, frames);
        return 0;
    }

    private static void Serve(AudioBuffer* buf, float* src, int available, uint want)
    {
        var dst = (float*)buf->Data;
        if (dst == null) { buf->Data = src; buf->DataByteSize = want * 4; return; }   // AU let us supply the pointer
        uint n = Math.Min(want, (uint)Math.Max(0, available));
        if (src != null && n > 0) Buffer.MemoryCopy(src, dst, buf->DataByteSize, n * 4L);
        if (n < want) new Span<float>(dst + n, (int)(want - n)).Clear();
    }

    public double GetParameter(int index) =>
        (uint)index < (uint)_parameters.Count ? _parameters[index].Normalize(AuV3.ParamValue(_paramObjs[index])) : 0;

    public void SetParameter(int index, double normalized)
    {
        if ((uint)index < (uint)_parameters.Count)
            AuV3.SetParamValue(_paramObjs[index], (float)_parameters[index].Denormalize(normalized));
    }

    // State is the unit's fullState — an NSDictionary of property-list objects (toll-free CFDictionary), so it
    // serializes with the same binary-plist path as the v2 ClassInfo.
    public byte[] SaveState()
    {
        IntPtr dict = AuV3.FullState(_au);
        return dict == IntPtr.Zero ? [] : CoreFoundation.ToData(dict);
    }

    public void LoadState(ReadOnlySpan<byte> state)
    {
        if (state.Length == 0) return;
        IntPtr dict = CoreFoundation.CreatePropertyList(state);
        if (dict == IntPtr.Zero) return;
        try { AuV3.SetFullState(_au, dict); }
        finally { CoreFoundation.CFRelease(dict); }
    }

    // ── Editor: the AU's own custom view via AUAudioUnit's requestViewControllerWithCompletionHandler: ──
    // v3 AUs vend no kAudioUnitProperty_CocoaUI, so the custom view comes from the view controller. There is no
    // AUGenericView fallback here — it needs a v2 AudioUnit handle, which is null for an out-of-process v3 AU — so
    // HasEditor mirrors the unit's own providesUserInterface.
    public IPluginGui? Gui => this;

    bool IPluginGui.HasEditor => AuV3.ProvidesUserInterface(_au);

    bool IPluginGui.IsApiSupported(string windowApi, bool floating) => windowApi == "cocoa" && !floating;

    bool IPluginGui.Create(string windowApi, bool floating)
    {
        if (windowApi != "cocoa" || floating || _view != IntPtr.Zero) return _view != IntPtr.Zero;
        IntPtr vc = AuV3.RequestViewController(_au);   // main-thread; pumps for the main-queue completion
        if (vc == IntPtr.Zero) return false;
        IntPtr view = AuV3.ViewControllerView(vc);
        if (view == IntPtr.Zero) { AuAppKit.Release(vc); return false; }
        _viewController = vc;                          // retained by RequestViewController
        _view = AuAppKit.Retain(view);                 // own the view until Destroy
        return true;
    }

    bool IPluginGui.SetScale(double scale) => true;   // AppKit views are in points; the window handles backing scale

    bool IPluginGui.TryGetSize(out int width, out int height)
    {
        width = height = 0;
        if (_view == IntPtr.Zero) return false;
        CGRect f = AuAppKit.Frame(_view);
        width = (int)f.Width; height = (int)f.Height;
        return width > 0 && height > 0;
    }

    bool IPluginGui.SetParent(string windowApi, ulong windowHandle)
    {
        if (_view == IntPtr.Zero || windowHandle == 0) return false;
        AuAppKit.AddSubview((IntPtr)windowHandle, _view);
        AuAppKit.SetNeutralWindowBackground(AuAppKit.WindowOf((IntPtr)windowHandle));   // AU views are often transparent
        return true;
    }

    bool IPluginGui.Show() { if (_view != IntPtr.Zero) AuAppKit.SetHidden(_view, false); return true; }
    bool IPluginGui.Hide() { if (_view != IntPtr.Zero) AuAppKit.SetHidden(_view, true); return true; }

    void IPluginGui.Destroy()
    {
        if (_view != IntPtr.Zero) { AuAppKit.RemoveFromSuperview(_view); AuAppKit.Release(_view); _view = IntPtr.Zero; }
        if (_viewController != IntPtr.Zero) { AuAppKit.Release(_viewController); _viewController = IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ((IPluginGui)this).Destroy();
        Deactivate();
        foreach (IntPtr p in _paramObjs) AuAppKit.Release(p);
        _paramObjs.Clear();
        if (_midiBlock != IntPtr.Zero) { AuAppKit.Release(_midiBlock); _midiBlock = IntPtr.Zero; }
        if (_renderBlock != IntPtr.Zero) { AuAppKit.Release(_renderBlock); _renderBlock = IntPtr.Zero; }
        if (_au != IntPtr.Zero) { AuAppKit.Release(_au); _au = IntPtr.Zero; }
    }
}
