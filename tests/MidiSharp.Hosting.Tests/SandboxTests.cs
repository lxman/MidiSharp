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
    public void Sandboxed_scan_skips_a_crashing_plugin_and_resumes()
    {
        var worker = WorkerDll();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        var crashSrc = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "soundfonts", "clap-test", "crash.clap");
        var gain = GainDescriptor();
        Assert.SkipWhen(!File.Exists(crashSrc) || gain == null, "crash/gain CLAP fixtures not available.");

        var tmp = Path.Combine(Path.GetTempPath(), "midisharp-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.Copy(crashSrc, Path.Combine(tmp, "01_crash.clap"));        // sorts first → scanned (and crashes) first
            File.Copy(gain!.Path, Path.Combine(tmp, "02_gain.clap"));       // must still be found after resume

            var found = SandboxScanner.ScanFormat("CLAP", worker!, [tmp]);
            _out.WriteLine($"scan of [crasher, gain] found {found.Count}: {string.Join(", ", found.Select(p => p.Name))}");
            // The crasher killed its scan worker, but the scan resumed past it and discovered the good plugin.
            Assert.Contains(found, p => p.Id == "midisharp.test.gain");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
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
    public void Plugin_state_round_trips_through_the_worker()
    {
        var worker = WorkerDll();
        var desc = GainDescriptor();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        Assert.SkipWhen(desc == null, "CLAP gain fixture not installed.");

        using var plugin = new SandboxedPlugin(desc!, worker!, Config);
        using var effect = new HostedEffect(plugin, Config);
        var buf = new float[Block * 2];
        // SetParameter is delivered to the plugin on the next process block; render one to apply it.
        void Apply(double v) { plugin.SetParameter(0, v); Array.Clear(buf); effect.Process(buf); }

        Apply(0.25);
        var blob = plugin.SaveState();           // captures the plugin's state (gain = 0.25) across the worker
        _out.WriteLine($"saved {blob.Length} state bytes through the worker");
        Assert.NotEmpty(blob);                   // the gain fixture implements clap.state

        Apply(0.75);
        Assert.True(Math.Abs(plugin.GetParameter(0) - 0.75) < 1e-6, "parameter should have changed");

        plugin.LoadState(blob);                  // restore across the worker
        Assert.True(Math.Abs(plugin.GetParameter(0) - 0.25) < 1e-6, "loaded state should restore the parameter");
    }

    [Fact]
    public void A_hung_plugin_is_killed_by_the_watchdog_and_recovers()
    {
        var worker = WorkerDll();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        var f = new ClapFormat();
        var hang = f.Scan(f.DefaultSearchPaths).FirstOrDefault(p => p.Id == "midisharp.test.hang");
        Assert.SkipWhen(hang == null, "hang fixture not installed.");

        using var plugin = new SandboxedPlugin(hang!, worker!, Config);
        using var effect = new HostedEffect(plugin, Config);

        var buf = new float[Block * 2];
        Array.Fill(buf, 0.5f);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        effect.Process(buf);   // the worker hangs in process(); the watchdog must kill it
        sw.Stop();
        _out.WriteLine($"hung Process returned in {sw.ElapsedMilliseconds} ms; dead={plugin.IsDead}");

        // Bounded (not forever), latched dead, and silent — the host is never wedged by a hung plugin.
        Assert.True(sw.ElapsedMilliseconds < 5000, $"watchdog should bound a hung process call (took {sw.ElapsedMilliseconds} ms).");
        Assert.True(plugin.IsDead, "the proxy should latch dead after the watchdog kills a hung worker.");
        Assert.All(buf, v => Assert.Equal(0f, v));
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
