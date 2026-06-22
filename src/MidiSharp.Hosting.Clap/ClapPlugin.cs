using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
public sealed unsafe class ClapPlugin : IHostedPlugin, IPluginGui
{
    private readonly IntPtr _lib;
    private readonly ClapPluginEntry* _entry;
    private readonly ClapHost _host;
    private ClapAbi.ClapPlugin* _plugin;

    private ClapPluginParams* _params;
    private ClapPluginGui* _gui;
    private bool _guiCreated;
    private int _inputPortCount = 1;    // effects have one audio input; instruments typically zero
    private int _outputChannels = 2;    // 1 (mono, duplicated to the stereo bus) or 2
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
    private byte* _evBuffer;       // this block's CLAP events, back-to-back at a fixed max-size stride
    private int* _evOffsets;       // byte offset of event i within _evBuffer
    private int _evCapacity;       // max events per block
    private ClapEventParamValue* _liveParams;   // coalesced live (UI) param sets, emitted at time 0
    private int _liveCount;
    private long _steady;
    private bool _active;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct InEventsState { public int Count; public byte* Buffer; public int* Offsets; }

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

        QueryPorts();
        BuildParameters();
        _gui = (ClapPluginGui*)_plugin->GetExtension(_plugin, FixedConst(ExtGui));   // null when the plugin has no editor

        if (_plugin->Activate(_plugin, config.SampleRate, 1, max) == 0)   // [main-thread]
            throw new InvalidOperationException($"CLAP activate failed for '{Descriptor.Name}'.");

        // start_processing is a CLAP [audio-thread] call. The lock-step worker has no separate RT thread, so we
        // enter the audio-thread context (the same bracket Process uses) to keep clap.thread-check honest.
        _host.SetInProcess(true);
        int started = _plugin->StartProcessing(_plugin);
        _host.SetInProcess(false);
        if (started == 0)
            throw new InvalidOperationException($"CLAP start_processing failed for '{Descriptor.Name}'.");

        // Allocate the reusable process graph: 2-channel planar in/out, an empty-by-default event list.
        _inData32 = (float**)NativeMemory.AllocZeroed(2, (nuint)IntPtr.Size);
        _outData32 = (float**)NativeMemory.AllocZeroed(2, (nuint)IntPtr.Size);
        _inBuf = Alloc<ClapAudioBuffer>();
        _outBuf = Alloc<ClapAudioBuffer>();
        _inBuf->Data32 = _inData32; _inBuf->ChannelCount = 2;
        _outBuf->Data32 = _outData32; _outBuf->ChannelCount = (uint)_outputChannels;

        // Time-ordered event scratch: live param sets (≤ one per param) plus a generous per-block cap.
        // Every slot is the size of the largest event (param-value) so offsets stay 8-aligned for its double.
        _evCapacity = _parameters.Count + 512;
        _evBuffer = (byte*)NativeMemory.AllocZeroed((nuint)_evCapacity, (nuint)sizeof(ClapEventParamValue));
        _evOffsets = (int*)NativeMemory.AllocZeroed((nuint)_evCapacity, sizeof(int));
        _liveParams = (ClapEventParamValue*)NativeMemory.AllocZeroed((nuint)Math.Max(1, _parameters.Count), (nuint)sizeof(ClapEventParamValue));
        _evState = Alloc<InEventsState>();
        _evState->Count = 0; _evState->Buffer = _evBuffer; _evState->Offsets = _evOffsets;

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
        _process->AudioInputsCount = (uint)_inputPortCount;   // 0 for a typical instrument
        _process->AudioOutputsCount = 1;
        _process->InEvents = _inEvents;
        _process->OutEvents = _outEvents;
        _process->Transport = null;

        _active = true;
    }

    public void Deactivate()
    {
        if (!_active) return;

        // stop_processing is [audio-thread] (mirrors start_processing); deactivate is [main-thread].
        _host.SetInProcess(true);
        _plugin->StopProcessing(_plugin);
        _host.SetInProcess(false);
        _plugin->Deactivate(_plugin);

        if (_inData32 != null) NativeMemory.Free(_inData32);
        if (_outData32 != null) NativeMemory.Free(_outData32);
        Free(ref _inBuf); Free(ref _outBuf); Free(ref _process);
        Free(ref _inEvents); Free(ref _outEvents); Free(ref _evState);
        if (_evBuffer != null) { NativeMemory.Free(_evBuffer); _evBuffer = null; }
        if (_evOffsets != null) { NativeMemory.Free(_evOffsets); _evOffsets = null; }
        if (_liveParams != null) { NativeMemory.Free(_liveParams); _liveParams = null; }
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

        // Build the block's time-ordered event list: live param sets at time 0, then the caller's events
        // (assumed sorted by SampleOffset, as CLAP requires) converted to CLAP param/MIDI events.
        var n = 0;
        for (var i = 0; i < _liveCount && n < _evCapacity; i++, n++)
            WriteParamSlot(n, _liveParams[i].ParamId, _liveParams[i].Value, 0);
        foreach (HostEvent e in events)
        {
            if (n >= _evCapacity) break;
            if (e.Kind == HostEventKind.Param && (uint)e.ParamIndex < (uint)_paramIds.Count)
                WriteParamSlot(n++, _paramIds[e.ParamIndex], _parameters[e.ParamIndex].Denormalize(e.ParamValue), (uint)e.SampleOffset);
            else if (e.Kind == HostEventKind.Midi)
                WriteMidiSlot(n++, e.Status, e.Data1, e.Data2, (uint)e.SampleOffset);
        }
        _evState->Count = n;
        _host.SetInProcess(true);          // so clap.thread-check answers is_audio_thread() correctly
        _plugin->Process(_plugin, _process);
        _host.SetInProcess(false);
        _evState->Count = 0;
        _liveCount = 0;

        // A mono plugin wrote only channel 0; mirror it to channel 1 to fill the stereo bus.
        if (_outputChannels == 1)
            new ReadOnlySpan<float>(output.Channel(0), output.Frames).CopyTo(new Span<float>(output.Channel(1), output.Frames));
    }

    private void WriteParamSlot(int slot, uint paramId, double value, uint time)
    {
        int stride = sizeof(ClapEventParamValue);
        var e = (ClapEventParamValue*)(_evBuffer + slot * stride);
        e->Header.Size = (uint)sizeof(ClapEventParamValue);
        e->Header.Time = time; e->Header.SpaceId = CoreEventSpaceId; e->Header.Type = EventParamValue; e->Header.Flags = 0;
        e->ParamId = paramId; e->Cookie = null; e->NoteId = -1; e->PortIndex = -1; e->Channel = -1; e->Key = -1; e->Value = value;
        _evOffsets[slot] = slot * stride;
    }

    private void WriteMidiSlot(int slot, byte d0, byte d1, byte d2, uint time)
    {
        int stride = sizeof(ClapEventParamValue);
        var e = (ClapEventMidi*)(_evBuffer + slot * stride);
        e->Header.Size = (uint)sizeof(ClapEventMidi);
        e->Header.Time = time; e->Header.SpaceId = CoreEventSpaceId; e->Header.Type = EventMidi; e->Header.Flags = 0;
        e->PortIndex = 0; e->Data0 = d0; e->Data1 = d1; e->Data2 = d2;
        _evOffsets[slot] = slot * stride;
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
        uint id = _paramIds[index];
        double value = _parameters[index].Denormalize(normalized);

        // Coalesce live (UI) sets: one pending param-value per id, delivered at time 0 next block.
        for (var i = 0; i < _liveCount; i++)
            if (_liveParams[i].ParamId == id) { _liveParams[i].Value = value; return; }
        if (_liveCount >= _parameters.Count) return;

        ref ClapEventParamValue e = ref _liveParams[_liveCount++];
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

    public byte[] SaveState()
    {
        var ext = (ClapPluginState*)_plugin->GetExtension(_plugin, FixedConst(ExtState));
        if (ext == null) return [];
        using var ms = new System.IO.MemoryStream();
        GCHandle handle = GCHandle.Alloc(ms);
        try
        {
            var os = new ClapOStream { Ctx = (void*)GCHandle.ToIntPtr(handle), Write = &OStreamWrite };
            return ext->Save(_plugin, &os) != 0 ? ms.ToArray() : [];
        }
        finally { handle.Free(); }
    }

    public void LoadState(ReadOnlySpan<byte> state)
    {
        if (!_active || state.IsEmpty) return;
        var ext = (ClapPluginState*)_plugin->GetExtension(_plugin, FixedConst(ExtState));
        if (ext == null) return;
        var reader = new StateReader(state.ToArray());
        GCHandle handle = GCHandle.Alloc(reader);
        try
        {
            var ins = new ClapIStream { Ctx = (void*)GCHandle.ToIntPtr(handle), Read = &IStreamRead };
            ext->Load(_plugin, &ins);
        }
        finally { handle.Free(); }
    }

    private sealed class StateReader(byte[] data)
    {
        public readonly byte[] Data = data;
        public int Pos;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long OStreamWrite(ClapOStream* stream, void* buffer, ulong size)
    {
        var ms = (System.IO.MemoryStream)GCHandle.FromIntPtr((IntPtr)stream->Ctx).Target!;
        ms.Write(new ReadOnlySpan<byte>(buffer, (int)size));
        return (long)size;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long IStreamRead(ClapIStream* stream, void* buffer, ulong size)
    {
        var r = (StateReader)GCHandle.FromIntPtr((IntPtr)stream->Ctx).Target!;
        int n = Math.Min((int)size, r.Data.Length - r.Pos);
        if (n <= 0) return 0;
        r.Data.AsSpan(r.Pos, n).CopyTo(new Span<byte>(buffer, n));
        r.Pos += n;
        return n;
    }

    // ── Editor (clap.gui) ──────────────────────────────────────────────────────────────────────────
    public IPluginGui? Gui => _gui != null ? this : null;

    bool IPluginGui.HasEditor => _gui != null;

    void IPluginGui.BindRunLoop(IEditorRunLoop? runLoop)
    {
        if (runLoop != null) _host.SetEditorContext(_plugin, runLoop);
        else _host.ClearEditorContext();
    }

    bool IPluginGui.IsApiSupported(string windowApi, bool floating)
        => _gui != null && _gui->IsApiSupported(_plugin, FixedConst(windowApi), (byte)(floating ? 1 : 0)) != 0;

    bool IPluginGui.Create(string windowApi, bool floating)
    {
        if (_gui == null) return false;
        _guiCreated = _gui->Create(_plugin, FixedConst(windowApi), (byte)(floating ? 1 : 0)) != 0;
        return _guiCreated;
    }

    bool IPluginGui.SetScale(double scale) => _gui != null && _gui->SetScale(_plugin, scale) != 0;

    bool IPluginGui.TryGetSize(out int width, out int height)
    {
        width = height = 0;
        if (_gui == null) return false;
        uint w, h;
        if (_gui->GetSize(_plugin, &w, &h) == 0) return false;
        width = (int)w; height = (int)h;
        return true;
    }

    bool IPluginGui.SetParent(string windowApi, ulong windowHandle)
    {
        if (_gui == null) return false;
        var win = new ClapWindow { Api = FixedConst(windowApi), Handle = (nuint)windowHandle };
        return _gui->SetParent(_plugin, &win) != 0;
    }

    bool IPluginGui.Show() => _gui != null && _gui->Show(_plugin) != 0;
    bool IPluginGui.Hide() => _gui != null && _gui->Hide(_plugin) != 0;

    void IPluginGui.Destroy()
    {
        if (_gui != null && _guiCreated) { _gui->Destroy(_plugin); _guiCreated = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_gui != null && _guiCreated) { _gui->Destroy(_plugin); _guiCreated = false; }
        Deactivate();
        if (_plugin != null) { _plugin->Destroy(_plugin); _plugin = null; }
        _host.Dispose();
        if (_lib != IntPtr.Zero) NativeLibrary.Free(_lib);
    }

    // Read the plugin's audio-port shape: record the input-port count (0 for a typical instrument) and
    // the main-output channel count (1 = mono, duplicated to the stereo bus; 2 = stereo). No port info →
    // assume one stereo in/out.
    private void QueryPorts()
    {
        var ext = (ClapAudioPorts*)_plugin->GetExtension(_plugin, FixedConst(ExtAudioPorts));
        if (ext == null) { _inputPortCount = 1; return; }
        _inputPortCount = (int)ext->Count(_plugin, 1);
        var info = default(ClapAudioPortInfo);
        if (ext->Count(_plugin, 0) >= 1 && ext->Get(_plugin, 0, 0, &info) != 0)
        {
            if (info.ChannelCount is 1 or 2) _outputChannels = (int)info.ChannelCount;
            else throw new NotSupportedException(
                $"CLAP '{Descriptor.Name}' main output is {info.ChannelCount}ch; only mono and stereo are supported.");
        }
    }

    private void BuildParameters()
    {
        _params = (ClapPluginParams*)_plugin->GetExtension(_plugin, FixedConst(ExtParams));
        _parameters.Clear();
        _paramIds.Clear();
        if (_params == null) return;

        uint count = _params->Count(_plugin);
        var info = default(ClapParamInfo);
        for (uint i = 0; i < count; i++)
        {
            if (_params->GetInfo(_plugin, i, &info) == 0) continue;
            const uint stepped = 1u << 0;   // CLAP_PARAM_IS_STEPPED
            string name = FixedStr(info.Name, 256);
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
        return st->Buffer + st->Offsets[index];
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
            if (!Const.TryGetValue(s, out IntPtr p))
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
                p = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                ((byte*)p)[bytes.Length] = 0;
                Const[s] = p;
            }
            return (byte*)p;
        }
    }
}
