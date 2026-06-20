using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.Clap.ClapAbi;

namespace MidiSharp.Hosting.Clap;

/// <summary>
/// A loaded CLAP effect. Maps the CLAP plugin onto <see cref="IHostedPlugin"/>: audio runs through a
/// pre-built <c>clap_process</c> over a stereo bus; parameters are read via the <c>clap.params</c>
/// extension and set by feeding <c>clap_event_param_value</c> events into the next process call (CLAP's
/// native way to change a parameter). State is deferred to Phase 2.
/// </summary>
/// <remarks>
/// Stereo (2-channel main ports) for now; a non-stereo main port throws <see cref="NotSupportedException"/>.
/// Parameter delivery via the per-block input-event list is single-producer for now (set off the audio
/// thread between blocks); a lock-free hand-off is Phase-3 work.
/// </remarks>
public sealed unsafe class ClapPlugin : IHostedPlugin
{
    private readonly IntPtr _lib;
    private readonly ClapPluginEntry* _entry;
    private readonly ClapHost _host;
    private ClapAbi.ClapPlugin* _plugin;

    private ClapPluginParams* _params;
    private readonly List<uint> _paramIds = [];
    private readonly List<PluginParameter> _parameters = [];

    // Pre-built unmanaged process graph (allocated in Activate, reused every block).
    private float** _inData32;
    private float** _outData32;
    private ClapAudioBuffer* _inBuf;
    private ClapAudioBuffer* _outBuf;
    private ClapProcess* _process;
    private ClapInputEvents* _inEvents;
    private ClapOutputEvents* _outEvents;
    private InEventsState* _evState;
    private ClapEventParamValue* _events;
    private int _pending;
    private long _steady;
    private bool _active;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct InEventsState { public int Count; public ClapEventParamValue* Events; }

    internal ClapPlugin(IntPtr lib, ClapPluginEntry* entry, ClapHost host,
        ClapAbi.ClapPlugin* plugin, PluginDescriptor descriptor, AudioConfig config)
    {
        _lib = lib;
        _entry = entry;
        _host = host;
        _plugin = plugin;
        Descriptor = descriptor;
        Activate(config);
    }

    public PluginDescriptor Descriptor { get; }
    public bool IsInstrument => Descriptor.IsInstrument;
    public IReadOnlyList<PluginParameter> Parameters => _parameters;

    public void Activate(AudioConfig config)
    {
        if (_active) return;
        var max = (uint)config.MaxBlockFrames;

        ValidateStereo();
        BuildParameters();

        if (_plugin->Activate(_plugin, config.SampleRate, 1, max) == 0)
            throw new InvalidOperationException($"CLAP activate failed for '{Descriptor.Name}'.");
        if (_plugin->StartProcessing(_plugin) == 0)
            throw new InvalidOperationException($"CLAP start_processing failed for '{Descriptor.Name}'.");

        // Allocate the reusable process graph: 2-channel planar in/out, an empty-by-default event list.
        _inData32 = (float**)NativeMemory.AllocZeroed(2, (nuint)IntPtr.Size);
        _outData32 = (float**)NativeMemory.AllocZeroed(2, (nuint)IntPtr.Size);
        _inBuf = Alloc<ClapAudioBuffer>();
        _outBuf = Alloc<ClapAudioBuffer>();
        _inBuf->Data32 = _inData32; _inBuf->ChannelCount = 2;
        _outBuf->Data32 = _outData32; _outBuf->ChannelCount = 2;

        var capacity = Math.Max(1, _parameters.Count);
        _events = (ClapEventParamValue*)NativeMemory.AllocZeroed((nuint)capacity, (nuint)sizeof(ClapEventParamValue));
        _evState = Alloc<InEventsState>();
        _evState->Count = 0; _evState->Events = _events;

        _inEvents = Alloc<ClapInputEvents>();
        _inEvents->Ctx = _evState;
        _inEvents->Size = &InEventsSize;
        _inEvents->Get = &InEventsGet;

        _outEvents = Alloc<ClapOutputEvents>();
        _outEvents->Ctx = null;
        _outEvents->TryPush = &OutEventsTryPush;

        _process = Alloc<ClapProcess>();
        _process->AudioInputs = _inBuf;
        _process->AudioOutputs = _outBuf;
        _process->AudioInputsCount = 1;
        _process->AudioOutputsCount = 1;
        _process->InEvents = _inEvents;
        _process->OutEvents = _outEvents;
        _process->Transport = null;

        _active = true;
    }

    public void Deactivate()
    {
        if (!_active) return;
        _plugin->StopProcessing(_plugin);
        _plugin->Deactivate(_plugin);

        if (_inData32 != null) NativeMemory.Free(_inData32);
        if (_outData32 != null) NativeMemory.Free(_outData32);
        Free(ref _inBuf); Free(ref _outBuf); Free(ref _process);
        Free(ref _inEvents); Free(ref _outEvents); Free(ref _evState);
        if (_events != null) { NativeMemory.Free(_events); _events = null; }
        _inData32 = _outData32 = null;
        _active = false;
    }

    public void Process(PlanarBuffers input, PlanarBuffers output, ReadOnlySpan<HostEvent> events)
    {
        if (!_active) return;

        _inData32[0] = input.Channel(0); _inData32[1] = input.Channel(1);
        _outData32[0] = output.Channel(0); _outData32[1] = output.Channel(1);
        _process->FramesCount = (uint)output.Frames;
        _process->SteadyTime = _steady;
        _steady += output.Frames;

        _evState->Count = _pending;     // hand this block's pending param changes to the plugin
        _plugin->Process(_plugin, _process);
        _evState->Count = 0;
        _pending = 0;
    }

    public double GetParameter(int index)
    {
        if (_params == null || (uint)index >= _parameters.Count) return 0;
        double v;
        if (_params->GetValue(_plugin, _paramIds[index], &v) == 0) return 0;
        return _parameters[index].Normalize(v);
    }

    public void SetParameter(int index, double normalized)
    {
        if (!_active || (uint)index >= _parameters.Count) return;
        var id = _paramIds[index];
        var value = _parameters[index].Denormalize(normalized);

        // Coalesce: one pending event per param id.
        for (var i = 0; i < _pending; i++)
            if (_events[i].ParamId == id) { _events[i].Value = value; return; }
        if (_pending >= _parameters.Count) return;

        ref var e = ref _events[_pending++];
        e.Header.Size = (uint)sizeof(ClapEventParamValue);
        e.Header.Time = 0;
        e.Header.SpaceId = CoreEventSpaceId;
        e.Header.Type = EventParamValue;
        e.Header.Flags = 0;
        e.ParamId = id;
        e.Cookie = null;
        e.NoteId = -1; e.PortIndex = -1; e.Channel = -1; e.Key = -1;
        e.Value = value;
    }

    public byte[] SaveState() => [];                       // Phase 2 (clap.state + persistence)
    public void LoadState(ReadOnlySpan<byte> state) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
        if (_plugin != null) { _plugin->Destroy(_plugin); _plugin = null; }
        _host.Dispose();
        if (_lib != IntPtr.Zero) NativeLibrary.Free(_lib);
    }

    private void ValidateStereo()
    {
        var ext = (ClapAudioPorts*)_plugin->GetExtension(_plugin, FixedConst(ExtAudioPorts));
        if (ext == null) return;   // no port info → assume the conventional stereo in/out
        var info = default(ClapAudioPortInfo);
        if (ext->Count(_plugin, 0) >= 1 && ext->Get(_plugin, 0, 0, &info) != 0 && info.ChannelCount != 2)
            throw new NotSupportedException(
                $"CLAP '{Descriptor.Name}' main output is {info.ChannelCount}ch; only stereo is supported for now.");
    }

    private void BuildParameters()
    {
        _params = (ClapPluginParams*)_plugin->GetExtension(_plugin, FixedConst(ExtParams));
        _parameters.Clear();
        _paramIds.Clear();
        if (_params == null) return;

        var count = _params->Count(_plugin);
        var info = default(ClapParamInfo);
        for (uint i = 0; i < count; i++)
        {
            if (_params->GetInfo(_plugin, i, &info) == 0) continue;
            const uint stepped = 1u << 0;   // CLAP_PARAM_IS_STEPPED
            var name = FixedStr(info.Name, 256);
            _parameters.Add(new PluginParameter((int)_paramIds.Count, name, label: "",
                info.MinValue, info.MaxValue, info.DefaultValue, isStepped: (info.Flags & stepped) != 0));
            _paramIds.Add(info.Id);
        }
    }

    // ── host-supplied event-list callbacks (read the per-plugin queue via ctx) ──────────────────────
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint InEventsSize(ClapInputEvents* list) => (uint)((InEventsState*)list->Ctx)->Count;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* InEventsGet(ClapInputEvents* list, uint index)
    {
        var st = (InEventsState*)list->Ctx;
        return &st->Events[index];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OutEventsTryPush(ClapOutputEvents* list, void* ev) => 1;   // accept & ignore

    // ── small unmanaged helpers ─────────────────────────────────────────────────────────────────────
    private static T* Alloc<T>() where T : unmanaged => (T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
    private static void Free<T>(ref T* p) where T : unmanaged { if (p != null) { NativeMemory.Free(p); p = null; } }

    private static readonly Dictionary<string, IntPtr> Const = new();
    private static byte* FixedConst(string s)
    {
        lock (Const)
        {
            if (!Const.TryGetValue(s, out var p))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                p = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                ((byte*)p)[bytes.Length] = 0;
                Const[s] = p;
            }
            return (byte*)p;
        }
    }
}
