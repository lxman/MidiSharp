using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MidiSharp.Hosting.AudioUnit.AudioUnitAbi;

namespace MidiSharp.Hosting.AudioUnit;

/// <summary>
/// A loaded Audio Unit, mapped onto <see cref="IHostedPlugin"/>. AU is a <b>pull</b> format: the host calls
/// <c>AudioUnitRender</c> and the unit pulls its input from a host-registered render callback. The engine is
/// <b>push</b> (<see cref="Process"/> hands us input and a place to put output). This adapter bridges the two —
/// it stashes the block's input channels, registers an <see cref="InputCallback"/> that serves them, and points
/// the output buffer list at the engine's output channels before rendering. Audio is non-interleaved float32,
/// so each channel maps straight onto a <see cref="PlanarBuffers"/> channel with no copy on the output side.
/// </summary>
/// <remarks>
/// Parameters (Task 4) and state (Task 5) are not yet wired: <see cref="Parameters"/> is empty and
/// <see cref="SaveState"/> returns <c>[]</c>, which the <see cref="IHostedPlugin"/> contract treats as
/// "unsupported" — correct behavior for the capabilities built so far. The editor (<see cref="Gui"/>) arrives
/// in Plan C.
/// </remarks>
public sealed unsafe class AudioUnitPlugin : IHostedPlugin
{
    private IntPtr _au;
    private GCHandle _self;                          // refCon for the unmanaged input callback
    private readonly List<PluginParameter> _parameters = [];
    private readonly List<uint> _paramIds = [];     // parallel to _parameters: the AudioUnitParameterID per index

    private float* _inCh0, _inCh1;                   // this block's input channels (read by the callback)
    private int _inFrames;
    private long _steady;
    private bool _active, _disposed;

    internal AudioUnitPlugin(IntPtr au, PluginDescriptor descriptor, AudioConfig config)
    {
        _au = au;
        Descriptor = descriptor;
        _self = GCHandle.Alloc(this);               // must exist before the callback refCon is set
        Activate(config);
    }

    public PluginDescriptor Descriptor { get; }
    public bool IsInstrument => Descriptor.IsInstrument;
    public IReadOnlyList<PluginParameter> Parameters => _parameters;   // populated in Task 4

    public void Activate(AudioConfig config)
    {
        if (_active) return;

        AudioStreamBasicDescription asbd = StereoFloat(config.SampleRate);
        SetProp(PropStreamFormat, ScopeOutput, &asbd, (uint)sizeof(AudioStreamBasicDescription));

        // Effects have an audio input bus fed by the pull callback; instruments (Plan B) do not.
        if (!IsInstrument)
        {
            SetProp(PropStreamFormat, ScopeInput, &asbd, (uint)sizeof(AudioStreamBasicDescription));
            var cb = new AURenderCallbackStruct { InputProc = &InputCallback, InputProcRefCon = (void*)GCHandle.ToIntPtr(_self) };
            SetProp(PropSetRenderCallback, ScopeInput, &cb, (uint)sizeof(AURenderCallbackStruct));
        }

        uint maxFrames = (uint)config.MaxBlockFrames;
        SetProp(PropMaximumFramesPerSlice, ScopeGlobal, &maxFrames, sizeof(uint));

        int st = AudioUnitInitialize(_au);
        if (st != 0) throw new InvalidOperationException($"AudioUnitInitialize failed ({st}) for '{Descriptor.Name}'.");
        BuildParameters();
        _active = true;
    }

    // Read the unit's global parameter list and build a normalized PluginParameter (+ the native id) per entry.
    private void BuildParameters()
    {
        _parameters.Clear();
        _paramIds.Clear();

        uint bytes = 0;
        if (AudioUnitGetPropertyInfo(_au, PropParameterList, ScopeGlobal, 0, &bytes, null) != 0 || bytes == 0) return;
        int count = (int)(bytes / sizeof(uint));
        var ids = new uint[count];
        fixed (uint* idp = ids)
        {
            uint size = bytes;
            if (AudioUnitGetProperty(_au, PropParameterList, ScopeGlobal, 0, idp, &size) != 0) return;
        }

        foreach (uint id in ids)
        {
            AudioUnitParameterInfo info;
            uint size = (uint)sizeof(AudioUnitParameterInfo);
            if (AudioUnitGetProperty(_au, PropParameterInfo, ScopeGlobal, id, &info, &size) != 0) continue;

            string name = info.CfNameString != IntPtr.Zero ? CoreFoundation.ToManaged(info.CfNameString) : ReadName(&info);
            bool stepped = info.Unit is ParamUnitIndexed or ParamUnitBoolean;
            _parameters.Add(new PluginParameter(_paramIds.Count, name, label: "",
                info.MinValue, info.MaxValue, info.DefaultValue, isStepped: stepped));
            _paramIds.Add(id);
        }
    }

    private static string ReadName(AudioUnitParameterInfo* info) => Marshal.PtrToStringUTF8((IntPtr)info->Name) ?? "";

    public void Deactivate()
    {
        if (!_active) return;
        AudioUnitUninitialize(_au);
        _active = false;
    }

    public void Process(PlanarBuffers input, PlanarBuffers output, ReadOnlySpan<HostEvent> events)
    {
        if (!_active) return;

        // Sample-accurate parameter automation: AudioUnitSetParameter is realtime-safe and the buffer-offset
        // schedules the change within this block.
        foreach (HostEvent e in events)
            if (e.Kind == HostEventKind.Param && (uint)e.ParamIndex < (uint)_parameters.Count)
                AudioUnitSetParameter(_au, _paramIds[e.ParamIndex], ScopeGlobal, 0,
                    (float)_parameters[e.ParamIndex].Denormalize(e.ParamValue), (uint)e.SampleOffset);

        // Stash this block's input for the pull callback (mono in → both sides; no input → silence).
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

        AudioUnitRender(_au, &flags, &ts, 0, frames, &abl);   // pulls input via InputCallback, writes our output
        _steady += frames;
    }

    // The "pull" input source: copy this block's stashed input into the buffers the AU asks us to fill.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int InputCallback(void* refCon, uint* ioActionFlags, AudioTimeStamp* ts, uint bus, uint frames, void* ioData)
    {
        var self = (AudioUnitPlugin)GCHandle.FromIntPtr((IntPtr)refCon).Target!;
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
        if (n < want) new Span<float>(dst + n, (int)(want - n)).Clear();              // zero-pad any shortfall
    }

    public double GetParameter(int index)
    {
        if ((uint)index >= (uint)_parameters.Count) return 0;
        float v;
        if (AudioUnitGetParameter(_au, _paramIds[index], ScopeGlobal, 0, &v) != 0) return 0;
        return _parameters[index].Normalize(v);
    }

    public void SetParameter(int index, double normalized)
    {
        if ((uint)index >= (uint)_parameters.Count) return;
        float v = (float)_parameters[index].Denormalize(normalized);
        AudioUnitSetParameter(_au, _paramIds[index], ScopeGlobal, 0, v, 0);
    }

    // State: not yet wired (Task 5). [] is the IHostedPlugin "unsupported" return.
    public byte[] SaveState() => [];
    public void LoadState(ReadOnlySpan<byte> state) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
        if (_au != IntPtr.Zero) { AudioComponentInstanceDispose(_au); _au = IntPtr.Zero; }
        if (_self.IsAllocated) _self.Free();
    }

    private static AudioStreamBasicDescription StereoFloat(int sampleRate) => new()
    {
        SampleRate = sampleRate,
        FormatId = FormatLinearPcm,
        FormatFlags = FormatFlagsNativeFloatNonInterleaved,
        BytesPerPacket = 4,        // one channel's bytes (non-interleaved)
        FramesPerPacket = 1,
        BytesPerFrame = 4,
        ChannelsPerFrame = 2,
        BitsPerChannel = 32,
    };

    private void SetProp(uint id, uint scope, void* data, uint size)
    {
        int st = AudioUnitSetProperty(_au, id, scope, 0, data, size);
        if (st != 0) throw new InvalidOperationException($"AudioUnitSetProperty(id={id}, scope={scope}) failed ({st}) for '{Descriptor.Name}'.");
    }
}
