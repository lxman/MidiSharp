using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static MidiSharp.Hosting.Ladspa.LadspaInterop;

namespace MidiSharp.Hosting.Ladspa;

/// <summary>
/// A loaded LADSPA effect. Maps the plugin's ports to a stereo insert: a native stereo (2-in/2-out)
/// plugin runs as a single instance; a mono (1-in/1-out) plugin runs as two instances (one per channel)
/// so left/right stay independent. Control-input ports are surfaced as <see cref="PluginParameter"/>s.
/// LADSPA has no MIDI/state, so events are ignored and save/load are empty.
/// </summary>
public sealed unsafe class LadspaPlugin : IHostedPlugin
{
    private readonly IntPtr _lib;             // owned; freed on dispose
    private readonly IntPtr _descriptorPtr;   // instantiate()'s first argument

    private readonly InstantiateFn _instantiate;
    private readonly ConnectPortFn _connect;
    private readonly ActivateFn? _activate;
    private readonly RunFn _run;
    private readonly DeactivateFn? _deactivate;
    private readonly CleanupFn _cleanup;

    private readonly List<int> _audioIn = [];
    private readonly List<int> _audioOut = [];
    private readonly List<ControlPort> _controls = [];   // all control ports (in + out)
    private readonly List<int> _paramToControl = [];      // PluginParameter.Index -> _controls index (inputs only)
    private readonly List<PluginParameter> _parameters = [];

    private IntPtr[] _handles = [];
    private UnmanagedFloatBuffer? _controlCells;          // one float per control port
    private int _sampleRate;
    private bool _dualMono;
    private bool _active;
    private bool _disposed;

    private readonly record struct ControlPort(int Port, bool IsInput, string Name, LADSPA_PortRangeHint Hint);

    internal LadspaPlugin(IntPtr lib, IntPtr descriptorPtr, LADSPA_Descriptor raw,
        PluginDescriptor descriptor, AudioConfig config)
    {
        _lib = lib;
        _descriptorPtr = descriptorPtr;
        Descriptor = descriptor;

        _instantiate = Marshal.GetDelegateForFunctionPointer<InstantiateFn>(raw.Instantiate);
        _connect = Marshal.GetDelegateForFunctionPointer<ConnectPortFn>(raw.ConnectPort);
        _activate = raw.Activate != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<ActivateFn>(raw.Activate) : null;
        _run = Marshal.GetDelegateForFunctionPointer<RunFn>(raw.Run);
        _deactivate = raw.Deactivate != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<DeactivateFn>(raw.Deactivate) : null;
        _cleanup = Marshal.GetDelegateForFunctionPointer<CleanupFn>(raw.Cleanup);

        ParsePorts(raw);
        Activate(config);
    }

    public PluginDescriptor Descriptor { get; }
    public bool IsInstrument => false;
    public IReadOnlyList<PluginParameter> Parameters => _parameters;

    public void Activate(AudioConfig config)
    {
        if (_active) return;
        _sampleRate = config.SampleRate;
        int channels = config.ChannelCount;

        // Pick a run mode for a stereo bus from the plugin's audio-port shape.
        if (_audioIn.Count == channels && _audioOut.Count == channels) _dualMono = false;
        else if (_audioIn.Count == 1 && _audioOut.Count == 1) _dualMono = true;
        else throw new NotSupportedException(
            $"LADSPA '{Descriptor.Name}' has {_audioIn.Count} in / {_audioOut.Count} out ports; " +
            "only stereo (2/2) and mono (1/1) effects are supported for a stereo bus.");

        // One float cell per control port; seed input controls to their hinted defaults.
        _controlCells = new UnmanagedFloatBuffer(_controls.Count);
        Span<float> cells = _controlCells.Span;
        for (var i = 0; i < _controls.Count; i++)
            cells[i] = _controls[i].IsInput ? ResolveDefault(_controls[i].Hint, _sampleRate) : 0f;

        // Build (or rebuild) the parameter list now that the sample rate is known (some bounds scale by it).
        _parameters.Clear();
        _paramToControl.Clear();
        for (var i = 0; i < _controls.Count; i++)
        {
            if (!_controls[i].IsInput) continue;
            _parameters.Add(BuildParameter(_parameters.Count, _controls[i].Hint, _controls[i].Name));
            _paramToControl.Add(i);
        }

        // Instantiate one handle (stereo) or one per channel (dual mono), and wire control ports.
        int voices = _dualMono ? channels : 1;
        _handles = new IntPtr[voices];
        for (var v = 0; v < voices; v++)
        {
            IntPtr handle = _instantiate(_descriptorPtr, (nuint)_sampleRate);
            if (handle == IntPtr.Zero) throw new InvalidOperationException($"LADSPA instantiate failed for '{Descriptor.Name}'.");
            _handles[v] = handle;
            for (var i = 0; i < _controls.Count; i++)
                _connect(handle, (nuint)_controls[i].Port, _controlCells.Pointer + i);
            _activate?.Invoke(handle);
        }
        _active = true;
    }

    public void Deactivate()
    {
        if (!_active) return;
        foreach (IntPtr h in _handles)
        {
            if (h == IntPtr.Zero) continue;
            _deactivate?.Invoke(h);
            _cleanup(h);
        }
        _handles = [];
        _controlCells?.Dispose();
        _controlCells = null;
        _active = false;
    }

    public void Process(PlanarBuffers input, PlanarBuffers output, ReadOnlySpan<HostEvent> events)
    {
        if (!_active) return;
        var frames = (nuint)output.Frames;

        if (_dualMono)
        {
            for (var c = 0; c < _handles.Length; c++)
            {
                _connect(_handles[c], (nuint)_audioIn[0], input.Channel(c));
                _connect(_handles[c], (nuint)_audioOut[0], output.Channel(c));
                _run(_handles[c], frames);
            }
        }
        else
        {
            IntPtr h = _handles[0];
            for (var c = 0; c < _audioIn.Count; c++) _connect(h, (nuint)_audioIn[c], input.Channel(c));
            for (var c = 0; c < _audioOut.Count; c++) _connect(h, (nuint)_audioOut[c], output.Channel(c));
            _run(h, frames);
        }
    }

    public double GetParameter(int index)
    {
        if (_controlCells == null || (uint)index >= _parameters.Count) return 0;
        float value = _controlCells.Span[_paramToControl[index]];
        return _parameters[index].Normalize(value);
    }

    public void SetParameter(int index, double normalized)
    {
        if (_controlCells == null || (uint)index >= _parameters.Count) return;
        // Plain write into the control cell the plugin reads each run() — realtime-safe for a float.
        _controlCells.Span[_paramToControl[index]] = (float)_parameters[index].Denormalize(normalized);
    }

    public byte[] SaveState() => [];                       // LADSPA has no opaque state
    public void LoadState(ReadOnlySpan<byte> state) { }    // parameters carry all of it

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
        if (_lib != IntPtr.Zero) NativeLibrary.Free(_lib);
    }

    private void ParsePorts(LADSPA_Descriptor raw)
    {
        var count = (int)raw.PortCount;
        int hintSize = Marshal.SizeOf<LADSPA_PortRangeHint>();
        for (var p = 0; p < count; p++)
        {
            int pd = Marshal.ReadInt32(raw.PortDescriptors, p * sizeof(int));
            string name = Str(Marshal.ReadIntPtr(raw.PortNames, p * IntPtr.Size));
            if (IsAudio(pd))
            {
                if (IsInput(pd)) _audioIn.Add(p);
                else if (IsOutput(pd)) _audioOut.Add(p);
            }
            else if (IsControl(pd))
            {
                var hint = Marshal.PtrToStructure<LADSPA_PortRangeHint>(raw.PortRangeHints + p * hintSize);
                _controls.Add(new ControlPort(p, IsInput(pd), name, hint));
            }
        }
    }

    private PluginParameter BuildParameter(int index, LADSPA_PortRangeHint hint, string name)
    {
        int h = hint.HintDescriptor;
        int scale = (h & HintSampleRate) != 0 ? _sampleRate : 1;
        bool toggled = (h & HintToggled) != 0;
        float min = toggled ? 0f : (h & HintBoundedBelow) != 0 ? hint.LowerBound * scale : 0f;
        float max = toggled ? 1f : (h & HintBoundedAbove) != 0 ? hint.UpperBound * scale : 1f;
        float def = ResolveDefault(hint, _sampleRate);
        return new PluginParameter(index, name, label: "", min, max, def,
            isStepped: toggled || (h & HintInteger) != 0,
            isLogarithmic: (h & HintLogarithmic) != 0);
    }
}
