using System;
using System.IO;
using System.Linq;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.Vst3;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// VST3 adapter: structural checks plus a live gate against the clean-room single-component gain fixture
/// (MidiSharpGain.vst3). Exercises the COM ABI end to end — bundle resolution, GetPluginFactory,
/// createInstance, IComponent/IAudioProcessor/IEditController, setupProcessing/activateBus/setActive, the
/// planar process call, and 0..1 parameters via the controller. Self-skips without the fixture.
/// </summary>
[Collection("EditorWindows")]
public sealed class Vst3Tests
{
    private const int Rate = 48000;
    private const int Block = 512;
    private static readonly AudioConfig Config = new(Rate, Block, ChannelCount: 2);

    private readonly Vst3Format _format = new();
    private readonly ITestOutputHelper _out;

    public Vst3Tests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Reports_name_and_paths_and_tolerates_a_missing_dir()
    {
        Assert.Equal("VST3", _format.Name);
        Assert.NotEmpty(_format.DefaultSearchPaths);
        Assert.Empty(_format.Scan([Path.Combine(Path.GetTempPath(), "midisharp-no-vst3-here")]).ToList());
    }

    private IHostedPlugin? LoadGain()
    {
        PluginDescriptor? d = _format.Scan(_format.DefaultSearchPaths).FirstOrDefault(p => p.Name == "MidiSharp VST3 Gain");
        return d == null ? null : _format.Load(d, Config);
    }

    private PluginDescriptor? FindSynth()
        => _format.Scan(_format.DefaultSearchPaths).FirstOrDefault(p => p.Name == "MidiSharp VST3 Synth");

    [Fact]
    public void Loads_the_vst3_fixture_and_reads_its_metadata()
    {
        IHostedPlugin? plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST3 gain fixture not installed.");
        using IHostedPlugin _ = plugin;
        _out.WriteLine($"Loaded {plugin!.Descriptor.Name}, {plugin.Parameters.Count} params");
        Assert.Equal("MidiSharp VST3 Gain", plugin.Descriptor.Name);
        Assert.Single(plugin.Parameters);
        Assert.Equal("Gain", plugin.Parameters[0].Name);
    }

    [Fact]
    public void Applies_its_parameter_through_the_bridge()
    {
        IHostedPlugin? plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST3 gain fixture not installed.");
        using var effect = new HostedEffect(plugin!, Config);

        const double amp = 0.4;
        double inputRms = amp / Math.Sqrt(2);

        double RenderRms(double normalized)
        {
            plugin!.SetParameter(0, normalized);
            effect.Reset();
            double sumSq = 0; long n = 0; var phase = 0.0;
            var buf = new float[Block * 2];
            for (var b = 0; b < 16; b++)
            {
                for (var i = 0; i < Block; i++)
                {
                    var s = (float)(amp * Math.Sin(phase));
                    phase += 2 * Math.PI * 1000.0 / Rate;
                    buf[2 * i] = s; buf[2 * i + 1] = s;
                }
                effect.Process(buf);
                foreach (float v in buf) { Assert.True(float.IsFinite(v), "non-finite"); sumSq += (double)v * v; n++; }
            }
            return Math.Sqrt(sumSq / n);
        }

        double unity = RenderRms(0.5);   // param 0.5 → ×1
        double half = RenderRms(0.25);   // param 0.25 → ×0.5
        double dbl = RenderRms(1.0);     // param 1.0 → ×2
        _out.WriteLine($"input={inputRms:F5} unity={unity:F5} half={half:F5} double={dbl:F5}");
        Assert.True(Math.Abs(unity - inputRms) < 0.01, $"×1 (got {unity:F5})");
        Assert.True(Math.Abs(half - inputRms * 0.5) < 0.01, $"×0.5 (got {half:F5})");
        Assert.True(Math.Abs(dbl - inputRms * 2.0) < 0.02, $"×2 (got {dbl:F5})");
    }

    [Fact]
    public void State_round_trips_through_an_ibstream()
    {
        IHostedPlugin? plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST3 gain fixture not installed.");
        using IHostedPlugin _ = plugin;

        plugin!.SetParameter(0, 0.25);          // a distinctive value
        byte[] saved = plugin.SaveState();         // component getState → IBStream → bytes
        Assert.NotEmpty(saved);                 // the fixture writes its 8-byte gain
        _out.WriteLine($"saved {saved.Length} bytes, param before = {plugin.GetParameter(0):F3}");

        plugin.SetParameter(0, 0.9);            // clobber it
        Assert.Equal(0.9, plugin.GetParameter(0), 3);

        plugin.LoadState(saved);                // IBStream → component setState restores the gain
        Assert.Equal(0.25, plugin.GetParameter(0), 3);
    }

    [Fact]
    public void Reports_its_editor_and_size_through_iplugview()
    {
        IHostedPlugin? plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST3 gain fixture not installed.");
        using IHostedPlugin _ = plugin;

        IPluginGui? gui = plugin!.Gui;
        Assert.NotNull(gui);
        Assert.True(gui!.HasEditor, "the gain fixture exposes an IPlugView editor.");
        Assert.True(gui.IsApiSupported("x11", floating: false), "the view should support X11 embedding.");
        Assert.True(gui.TryGetSize(out int w, out int h));
        Assert.Equal(300, w);
        Assert.Equal(200, h);
    }

    [Fact]
    public void Embeds_its_iplugview_editor_in_a_native_window()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")), "no X display.");
        IHostedPlugin? plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST3 gain fixture not installed.");
        using IHostedPlugin _ = plugin;

        using EditorWindow? window = MidiSharp.Hosting.EditorHost.EditorWindow.Open(plugin!.Gui, "VST3 editor test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor window should open (error: {window.Error}).");

        uint children = 0;
        for (var i = 0; i < 20 && children == 0; i++) { children = window.EmbeddedChildCount; if (children == 0) System.Threading.Thread.Sleep(50); }
        Assert.True(children >= 1, "the IPlugView should have attached a child window into the host window.");
        window.Close();
    }

    [Fact]
    public void Host_run_loop_drives_the_editors_timer()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")), "no X display.");
        IHostedPlugin? plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST3 gain fixture not installed.");
        using IHostedPlugin _ = plugin;

        // The fixture's editor registers a 20 ms timer with our Linux IRunLoop on attach, and bumps a tick
        // counter on each onTimer — exposed as the 2nd 8 bytes of its IBStream state. If the host's run loop
        // is pumping the plugin's timers, the count climbs while the window is open.
        using EditorWindow? window = MidiSharp.Hosting.EditorHost.EditorWindow.Open(plugin!.Gui, "VST3 run-loop test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor should open (error: {window.Error}).");

        System.Threading.Thread.Sleep(400);   // ~20 ticks at 20 ms if the loop is firing
        byte[] state = plugin.SaveState();
        window.Close();

        // SaveState wraps the component state: [int32 compLen][gain double][ticks double][int32 ctrlLen].
        Assert.True(state.Length >= 20, "fixture state should carry gain + tick count.");
        var compLen = BitConverter.ToInt32(state, 0);
        Assert.True(compLen >= 16, $"component state should be 16 bytes (got {compLen}).");
        var ticks = BitConverter.ToDouble(state, 12);   // 4 (len) + 8 (gain)
        Assert.True(ticks > 3, $"the host run loop should have fired the editor's timer several times (ticks={ticks}).");
    }

    [Fact]
    public void Parameters_marshal_to_the_ui_thread_while_the_editor_is_open()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")), "no X display.");
        IHostedPlugin? plugin = LoadGain();
        Assert.SkipWhen(plugin == null, "VST3 gain fixture not installed.");
        using IHostedPlugin _ = plugin;

        using EditorWindow? window = MidiSharp.Hosting.EditorHost.EditorWindow.Open(plugin!.Gui, "VST3 param-marshal test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor should open (error: {window.Error}).");

        // With the editor open, get/setParamNormalized are marshaled onto the editor UI thread (these calls
        // come from this — a different — thread). They must still round-trip.
        plugin.SetParameter(0, 0.3);
        Assert.Equal(0.3, plugin.GetParameter(0), 3);
        plugin.SetParameter(0, 0.8);
        Assert.Equal(0.8, plugin.GetParameter(0), 3);

        window.Close();
        // After close the run loop is unbound; params go direct again and still work.
        plugin.SetParameter(0, 0.5);
        Assert.Equal(0.5, plugin.GetParameter(0), 3);
    }

    [Fact]
    public void Discovers_the_instrument_and_its_separate_controller_parameter()
    {
        PluginDescriptor? d = FindSynth();
        Assert.SkipWhen(d == null, "VST3 synth fixture not installed.");
        Assert.True(d!.IsInstrument, "the synth's 'Instrument' subcategory should mark it an instrument.");

        using IHostedPlugin plugin = _format.Load(d, Config);   // the component exposes no controller → host creates the separate class
        _out.WriteLine($"Loaded {plugin.Descriptor.Name}, instrument={plugin.IsInstrument}, {plugin.Parameters.Count} params");
        Assert.Single(plugin.Parameters);             // the param lives on the SEPARATE controller object
        Assert.Equal("Volume", plugin.Parameters[0].Name);
    }

    [Fact]
    public void Plays_a_note_through_the_event_list()
    {
        PluginDescriptor? d = FindSynth();
        Assert.SkipWhen(d == null, "VST3 synth fixture not installed.");
        using var inst = new HostedInstrument(_format.Load(d!, Config), Config);

        // Silent until a note arrives; then an A4 (key 69) sounds at ~440 Hz via the VST3 event list.
        var buf = new float[Block * 2];
        inst.Render(buf);
        double preRms = 0; foreach (float v in buf) preRms += (double)v * v;
        Assert.True(Math.Sqrt(preRms / buf.Length) < 1e-6, "instrument should be silent before any note.");

        inst.NoteOn(0, channel: 0, key: 69, velocity: 100);
        const int blocks = 64;
        var left = new float[blocks * Block];
        for (var b = 0; b < blocks; b++)
        {
            Array.Clear(buf);
            inst.Render(buf);
            for (var i = 0; i < Block; i++) left[b * Block + i] = buf[2 * i];
        }

        double rms = 0; var crossings = 0;
        for (var i = 0; i < left.Length; i++) rms += (double)left[i] * left[i];
        rms = Math.Sqrt(rms / left.Length);
        for (var i = 1; i < left.Length; i++)
            if ((left[i - 1] < 0f && left[i] >= 0f) || (left[i - 1] >= 0f && left[i] < 0f)) crossings++;
        double hz = crossings * (double)Rate / (2.0 * left.Length);
        _out.WriteLine($"note rms={rms:F4} freq={hz:F1}");
        Assert.True(rms > 0.1, $"the note should sound through the event list (rms {rms:F4}).");
        Assert.True(Math.Abs(hz - 440.0) < 5.0, $"A4 should sound at ~440 Hz (measured {hz:F1}).");
    }
}
