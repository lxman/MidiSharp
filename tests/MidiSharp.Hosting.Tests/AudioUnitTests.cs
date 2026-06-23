using System;
using System.Linq;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using MidiSharp.Hosting.AudioUnit;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// The AU adapter against Apple's always-present system Audio Units — no third-party install, no fixtures.
/// Discovery surfaces <c>AULowpass</c> (effect) and <c>DLSMusicDevice</c> (instrument); loading <c>AULowpass</c>
/// and rendering through it proves the pull→push render shim (a low tone passes, a high tone is attenuated by
/// the filter). macOS-only.
/// </summary>
public sealed unsafe class AudioUnitTests
{
    [Fact]
    public void Discovers_apple_system_audio_units()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Audio Units are macOS-only.");

        var format = new AudioUnitFormat();
        var all = format.Scan(format.DefaultSearchPaths).ToList();

        Assert.Contains(all, d => d.Format == "AU" && d.Id.StartsWith("aufx:lpas", StringComparison.Ordinal));   // AULowpass
        PluginDescriptor? dls = all.FirstOrDefault(d => d.Id.StartsWith("aumu:dls", StringComparison.Ordinal));   // built-in synth
        Assert.NotNull(dls);
        Assert.True(dls!.IsInstrument, "DLSMusicDevice should report as an instrument.");
    }

    [Fact]
    public void Loads_aulowpass_and_filters_audio_through_the_render_shim()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Audio Units are macOS-only.");

        var format = new AudioUnitFormat();
        PluginDescriptor lowpass = format.Scan(format.DefaultSearchPaths)
            .First(d => d.Id.StartsWith("aufx:lpas", StringComparison.Ordinal));

        const int frames = 512;
        using IHostedPlugin plugin = format.Load(lowpass, new AudioConfig(48000, frames, 2));

        float* in0 = Alloc(frames), in1 = Alloc(frames), out0 = Alloc(frames), out1 = Alloc(frames);
        float** ins = AllocPtrs(in0, in1), outs = AllocPtrs(out0, out1);
        try
        {
            var input = new PlanarBuffers(ins, 2, frames);
            var output = new PlanarBuffers(outs, 2, frames);

            // Below the ~6.9 kHz default cutoff → passes; well above → attenuated. Matches the Task 0 spike.
            double passRms = RenderTone(plugin, input, output, in0, in1, out0, 200.0, frames);
            double stopRms = RenderTone(plugin, input, output, in0, in1, out0, 18000.0, frames);

            Assert.True(passRms > 0.1, $"a 200 Hz tone should pass the lowpass (out RMS={passRms:F4}) — proves input was pulled and output captured.");
            Assert.True(stopRms < passRms * 0.7, $"an 18 kHz tone should be attenuated (stop={stopRms:F4} vs pass={passRms:F4}) — proves the AU actually filtered.");
        }
        finally
        {
            NativeMemory.Free(in0); NativeMemory.Free(in1); NativeMemory.Free(out0); NativeMemory.Free(out1);
            NativeMemory.Free(ins); NativeMemory.Free(outs);
        }
    }

    [Fact]
    public void Sweeping_the_cutoff_parameter_changes_the_filter_output()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Audio Units are macOS-only.");

        var format = new AudioUnitFormat();
        PluginDescriptor lowpass = format.Scan(format.DefaultSearchPaths)
            .First(d => d.Id.StartsWith("aufx:lpas", StringComparison.Ordinal));

        const int frames = 512;
        using IHostedPlugin plugin = format.Load(lowpass, new AudioConfig(48000, frames, 2));

        Assert.NotEmpty(plugin.Parameters);
        int cutoff = plugin.Parameters
            .FirstOrDefault(p => p.Name.Contains("cutoff", StringComparison.OrdinalIgnoreCase))?.Index ?? 0;

        float* in0 = Alloc(frames), in1 = Alloc(frames), out0 = Alloc(frames), out1 = Alloc(frames);
        float** ins = AllocPtrs(in0, in1), outs = AllocPtrs(out0, out1);
        try
        {
            var input = new PlanarBuffers(ins, 2, frames);
            var output = new PlanarBuffers(outs, 2, frames);

            // 1 kHz sits below the default cutoff (~6.9 kHz), so it passes; dropping the cutoff to its minimum
            // pushes 1 kHz into the stopband.
            double atDefault = RenderTone(plugin, input, output, in0, in1, out0, 1000.0, frames);
            plugin.SetParameter(cutoff, 0.0);
            double atMinCutoff = RenderTone(plugin, input, output, in0, in1, out0, 1000.0, frames);

            Assert.True(atDefault > 0.1, $"1 kHz should pass at the default cutoff (out RMS={atDefault:F4}).");
            Assert.True(atMinCutoff < atDefault * 0.5,
                $"lowering the cutoff parameter should attenuate 1 kHz (min={atMinCutoff:F4} vs default={atDefault:F4}).");
        }
        finally
        {
            NativeMemory.Free(in0); NativeMemory.Free(in1); NativeMemory.Free(out0); NativeMemory.Free(out1);
            NativeMemory.Free(ins); NativeMemory.Free(outs);
        }
    }

    [Fact]
    public void Reports_a_cocoa_editor()
    {
        // The editor surface is AppKit-free (no view is created here); the actual embed is proven by the
        // main-thread MacEditorHarness, since xUnit can't host AppKit on the main thread.
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Audio Units are macOS-only.");

        var format = new AudioUnitFormat();
        PluginDescriptor lowpass = format.Scan(format.DefaultSearchPaths)
            .First(d => d.Id.StartsWith("aufx:lpas", StringComparison.Ordinal));
        using IHostedPlugin plugin = format.Load(lowpass, new AudioConfig(48000, 512, 2));

        IPluginGui? gui = plugin.Gui;
        Assert.NotNull(gui);
        Assert.True(gui!.HasEditor);
        Assert.True(gui.IsApiSupported("cocoa", floating: false));
        Assert.False(gui.IsApiSupported("cocoa", floating: true), "AU editors embed; they don't float.");
        Assert.False(gui.IsApiSupported("x11", floating: false), "AU editors are Cocoa-only.");
    }

    [Fact]
    public void Dls_instrument_renders_a_note()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Audio Units are macOS-only.");

        var format = new AudioUnitFormat();
        PluginDescriptor dls = format.Scan(format.DefaultSearchPaths)
            .First(d => d.Id.StartsWith("aumu:dls", StringComparison.Ordinal));   // DLSMusicDevice (built-in synth)
        Assert.True(dls.IsInstrument);

        const int frames = 512;
        using IHostedPlugin plugin = format.Load(dls, new AudioConfig(48000, frames, 2));
        Assert.True(plugin.IsInstrument);

        float* in0 = Alloc(frames), in1 = Alloc(frames), out0 = Alloc(frames), out1 = Alloc(frames);
        float** ins = AllocPtrs(in0, in1), outs = AllocPtrs(out0, out1);
        try
        {
            var input = new PlanarBuffers(ins, 2, frames);     // instruments have no input bus; unused
            var output = new PlanarBuffers(outs, 2, frames);

            double silent = RenderBlock(plugin, input, output, out0, ReadOnlySpan<HostEvent>.Empty);

            // Middle C, velocity 100, at the start of the block; let it ring for a few blocks.
            double playing = 0;
            for (int blk = 0; blk < 12; blk++)
            {
                ReadOnlySpan<HostEvent> ev = blk == 0 ? [HostEvent.Midi(0, 0x90, 60, 100)] : default;
                playing = RenderBlock(plugin, input, output, out0, ev);
            }

            Assert.True(playing > 1e-3, $"DLSMusicDevice should render audible output after a note-on (RMS={playing:F5}).");
            Assert.True(silent < playing * 0.5, $"output should be quiet before the note (silent={silent:F5}, playing={playing:F5}) — the note drove the sound.");
        }
        finally
        {
            NativeMemory.Free(in0); NativeMemory.Free(in1); NativeMemory.Free(out0); NativeMemory.Free(out1);
            NativeMemory.Free(ins); NativeMemory.Free(outs);
        }
    }

    private static double RenderBlock(IHostedPlugin plugin, PlanarBuffers input, PlanarBuffers output, float* out0, ReadOnlySpan<HostEvent> events)
    {
        plugin.Process(input, output, events);
        double sum = 0;
        for (int i = 0; i < output.Frames; i++) sum += out0[i] * (double)out0[i];
        return Math.Sqrt(sum / output.Frames);
    }

    [Fact]
    public void State_round_trips_through_classinfo()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Audio Units are macOS-only.");

        var format = new AudioUnitFormat();
        PluginDescriptor lowpass = format.Scan(format.DefaultSearchPaths)
            .First(d => d.Id.StartsWith("aufx:lpas", StringComparison.Ordinal));
        using IHostedPlugin plugin = format.Load(lowpass, new AudioConfig(48000, 512, 2));

        int cutoff = plugin.Parameters
            .FirstOrDefault(p => p.Name.Contains("cutoff", StringComparison.OrdinalIgnoreCase))?.Index ?? 0;

        plugin.SetParameter(cutoff, 0.7);
        double saved = plugin.GetParameter(cutoff);

        byte[] state = plugin.SaveState();
        Assert.NotEmpty(state);

        plugin.SetParameter(cutoff, 0.1);
        Assert.True(plugin.GetParameter(cutoff) < saved - 0.2, "the parameter should have moved away before reload.");

        plugin.LoadState(state);
        Assert.True(Math.Abs(plugin.GetParameter(cutoff) - saved) < 0.05,
            $"ClassInfo should restore the cutoff (got {plugin.GetParameter(cutoff):F3}, expected ~{saved:F3}).");
    }

    // Render 16 blocks of a sine (phase-continuous across blocks) so the filter settles; return the last block's
    // output RMS on channel 0.
    private static double RenderTone(IHostedPlugin plugin, PlanarBuffers input, PlanarBuffers output,
        float* in0, float* in1, float* out0, double freq, int frames)
    {
        const double sr = 48000.0, amp = 0.5;
        long pos = 0;
        double rms = 0;
        for (int blk = 0; blk < 16; blk++)
        {
            for (int i = 0; i < frames; i++)
            {
                var s = (float)(amp * Math.Sin(2.0 * Math.PI * freq * (pos + i) / sr));
                in0[i] = s; in1[i] = s;
            }
            plugin.Process(input, output, ReadOnlySpan<HostEvent>.Empty);
            pos += frames;
            if (blk == 15)
            {
                double sum = 0;
                for (int i = 0; i < frames; i++) sum += out0[i] * (double)out0[i];
                rms = Math.Sqrt(sum / frames);
            }
        }
        return rms;
    }

    private static float* Alloc(int frames) => (float*)NativeMemory.AllocZeroed((nuint)frames, sizeof(float));

    private static float** AllocPtrs(float* a, float* b)
    {
        var p = (float**)NativeMemory.Alloc(2, (nuint)IntPtr.Size);
        p[0] = a; p[1] = b;
        return p;
    }
}
