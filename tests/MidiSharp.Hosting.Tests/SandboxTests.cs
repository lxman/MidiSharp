using System;
using System.IO;
using System.Linq;
using MidiSharp.Hosting;
using MidiSharp.Hosting.Clap;
using MidiSharp.Hosting.Sandbox;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Out-of-process sandboxing (Phase 8): a plugin runs in a worker process behind the same IHostedPlugin
/// interface. Verifies correctness (the sandboxed gain fixture matches the in-process result) and crash
/// isolation (killing the worker makes the proxy emit silence without throwing — the host survives).
/// Self-skips when the worker exe or the CLAP gain fixture isn't available.
/// </summary>
public sealed class SandboxTests
{
    private const int Rate = 48000;
    private const int Block = 512;
    private static readonly AudioConfig Config = new(Rate, Block, ChannelCount: 2);

    private readonly ITestOutputHelper _out;
    public SandboxTests(ITestOutputHelper output) => _out = output;

    // The worker builds to its own bin (with its runtimeconfig + adapter deps); mirror this test's
    // config/tfm path over to it.
    private static string? WorkerDll()
    {
        var baseDir = AppContext.BaseDirectory;
        var workerDir = baseDir.Replace(
            Path.Combine("tests", "MidiSharp.Hosting.Tests"),
            Path.Combine("src", "MidiSharp.Hosting.Worker"));
        var dll = Path.Combine(workerDir, "MidiSharp.Hosting.Worker.dll");
        return File.Exists(dll) ? dll : null;
    }

    private static PluginDescriptor? GainDescriptor()
    {
        var f = new ClapFormat();
        return f.Scan(f.DefaultSearchPaths).FirstOrDefault(p => p.Id == "midisharp.test.gain");
    }

    [Fact]
    public void Sandboxed_scan_discovers_plugins_in_a_worker_process()
    {
        var worker = WorkerDll();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        var found = SandboxScanner.ScanFormat("CLAP", worker!);
        _out.WriteLine($"sandboxed CLAP scan found {found.Count} plugins");
        Assert.All(found, p => Assert.Equal("CLAP", p.Format));
        Assert.SkipWhen(GainDescriptor() == null, "CLAP gain fixture not installed.");
        Assert.Contains(found, p => p.Id == "midisharp.test.gain");
    }

    [Fact]
    public void Sandboxed_plugin_processes_audio_correctly_in_another_process()
    {
        var worker = WorkerDll();
        var desc = GainDescriptor();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        Assert.SkipWhen(desc == null, "CLAP gain fixture not installed.");

        using var plugin = new SandboxedPlugin(desc!, worker!, Config);
        _out.WriteLine($"sandboxed: {plugin.Descriptor.Name}, {plugin.Parameters.Count} params, dead={plugin.IsDead}");
        Assert.Equal("MidiSharp Test Gain", plugin.Descriptor.Name);
        Assert.Single(plugin.Parameters);

        using var effect = new HostedEffect(plugin, Config);
        const double amp = 0.4;
        var inputRms = amp / Math.Sqrt(2);

        double RenderRms(double normalized)
        {
            plugin.SetParameter(0, normalized);
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
                foreach (var v in buf) { Assert.True(float.IsFinite(v), "non-finite"); sumSq += (double)v * v; n++; }
            }
            return Math.Sqrt(sumSq / n);
        }

        var unity = RenderRms(0.5);
        var half = RenderRms(0.25);
        var dbl = RenderRms(1.0);
        _out.WriteLine($"input={inputRms:F5} unity={unity:F5} half={half:F5} double={dbl:F5}");
        Assert.True(Math.Abs(unity - inputRms) < 0.01, $"×1 (got {unity:F5})");
        Assert.True(Math.Abs(half - inputRms * 0.5) < 0.01, $"×0.5 (got {half:F5})");
        Assert.True(Math.Abs(dbl - inputRms * 2.0) < 0.02, $"×2 (got {dbl:F5})");
    }

    [Fact]
    public void A_worker_crash_degrades_to_silence_and_the_host_survives()
    {
        var worker = WorkerDll();
        var desc = GainDescriptor();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        Assert.SkipWhen(desc == null, "CLAP gain fixture not installed.");

        using var plugin = new SandboxedPlugin(desc!, worker!, Config);
        using var effect = new HostedEffect(plugin, Config);
        plugin.SetParameter(0, 0.5);

        var buf = new float[Block * 2];
        Array.Fill(buf, 0.5f);
        effect.Process(buf);
        Assert.False(plugin.IsDead);
        Assert.True(Math.Abs(buf[0] - 0.5f) < 1e-4, "before the crash the plugin should pass audio.");

        // Simulate a plugin crash: kill the worker. The next blocks must NOT throw — they go silent.
        plugin.KillWorkerForTesting();
        for (var b = 0; b < 4; b++)
        {
            Array.Fill(buf, 0.5f);
            effect.Process(buf);   // must not throw even though the worker is gone
        }
        Assert.True(plugin.IsDead, "the proxy should have latched dead after the worker died.");
        Assert.All(buf, v => Assert.Equal(0f, v));   // silence, not a crash
        _out.WriteLine("worker killed → proxy emits silence, host alive.");
    }
}
