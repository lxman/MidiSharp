using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
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
[Collection("EditorWindows")]
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
        string baseDir = AppContext.BaseDirectory;
        string workerDir = baseDir.Replace(
            Path.Combine("tests", "MidiSharp.Hosting.Tests"),
            Path.Combine("src", "MidiSharp.Hosting.Worker"));
        string dll = Path.Combine(workerDir, "MidiSharp.Hosting.Worker.dll");
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
        string? worker = WorkerDll();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        List<PluginDescriptor> found = SandboxScanner.ScanFormat("CLAP", worker!);
        _out.WriteLine($"sandboxed CLAP scan found {found.Count} plugins");
        Assert.All(found, p => Assert.Equal("CLAP", p.Format));
        Assert.SkipWhen(GainDescriptor() == null, "CLAP gain fixture not installed.");
        Assert.Contains(found, p => p.Id == "midisharp.test.gain");
    }

    [Fact]
    public void Sandboxed_scan_skips_a_crashing_plugin_and_resumes()
    {
        string? worker = WorkerDll();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        string crashSrc = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "soundfonts", "clap-test", "crash.clap");
        PluginDescriptor? gain = GainDescriptor();
        Assert.SkipWhen(!File.Exists(crashSrc) || gain == null, "crash/gain CLAP fixtures not available.");

        string tmp = Path.Combine(Path.GetTempPath(), "midisharp-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.Copy(crashSrc, Path.Combine(tmp, "01_crash.clap"));        // sorts first → scanned (and crashes) first
            File.Copy(gain!.Path, Path.Combine(tmp, "02_gain.clap"));       // must still be found after resume

            List<PluginDescriptor> found = SandboxScanner.ScanFormat("CLAP", worker!, [tmp]);
            _out.WriteLine($"scan of [crasher, gain] found {found.Count}: {string.Join(", ", found.Select(p => p.Name))}");
            // The crasher killed its scan worker, but the scan resumed past it and discovered the good plugin.
            Assert.Contains(found, p => p.Id == "midisharp.test.gain");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Sandboxed_plugin_processes_audio_correctly_in_another_process()
    {
        string? worker = WorkerDll();
        PluginDescriptor? desc = GainDescriptor();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        Assert.SkipWhen(desc == null, "CLAP gain fixture not installed.");

        using var plugin = new SandboxedPlugin(desc!, worker!, Config);
        _out.WriteLine($"sandboxed: {plugin.Descriptor.Name}, {plugin.Parameters.Count} params, dead={plugin.IsDead}");
        Assert.Equal("MidiSharp Test Gain", plugin.Descriptor.Name);
        Assert.Single(plugin.Parameters);

        using var effect = new HostedEffect(plugin, Config);
        const double amp = 0.4;
        double inputRms = amp / Math.Sqrt(2);

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
                foreach (float v in buf) { Assert.True(float.IsFinite(v), "non-finite"); sumSq += (double)v * v; n++; }
            }
            return Math.Sqrt(sumSq / n);
        }

        double unity = RenderRms(0.5);
        double half = RenderRms(0.25);
        double dbl = RenderRms(1.0);
        _out.WriteLine($"input={inputRms:F5} unity={unity:F5} half={half:F5} double={dbl:F5}");
        Assert.True(Math.Abs(unity - inputRms) < 0.01, $"×1 (got {unity:F5})");
        Assert.True(Math.Abs(half - inputRms * 0.5) < 0.01, $"×0.5 (got {half:F5})");
        Assert.True(Math.Abs(dbl - inputRms * 2.0) < 0.02, $"×2 (got {dbl:F5})");
    }

    [Fact]
    public void Plugin_state_round_trips_through_the_worker()
    {
        string? worker = WorkerDll();
        PluginDescriptor? desc = GainDescriptor();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        Assert.SkipWhen(desc == null, "CLAP gain fixture not installed.");

        using var plugin = new SandboxedPlugin(desc!, worker!, Config);
        using var effect = new HostedEffect(plugin, Config);
        var buf = new float[Block * 2];
        // SetParameter is delivered to the plugin on the next process block; render one to apply it.
        void Apply(double v) { plugin.SetParameter(0, v); Array.Clear(buf); effect.Process(buf); }

        Apply(0.25);
        byte[] blob = plugin.SaveState();           // captures the plugin's state (gain = 0.25) across the worker
        _out.WriteLine($"saved {blob.Length} state bytes through the worker");
        Assert.NotEmpty(blob);                   // the gain fixture implements clap.state

        Apply(0.75);
        Assert.True(Math.Abs(plugin.GetParameter(0) - 0.75) < 1e-6, "parameter should have changed");

        plugin.LoadState(blob);                  // restore across the worker
        Assert.True(Math.Abs(plugin.GetParameter(0) - 0.25) < 1e-6, "loaded state should restore the parameter");
    }

    [Fact]
    public void Opens_a_plugin_editor_in_the_worker_process()
    {
        string? worker = WorkerDll();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")), "no X display.");
        var f = new ClapFormat();
        PluginDescriptor? gui = f.Scan(f.DefaultSearchPaths).FirstOrDefault(p => p.Id == "midisharp.test.gui");
        Assert.SkipWhen(gui == null, "CLAP gui fixture not installed.");

        using var plugin = new SandboxedPlugin(gui!, worker!, Config);
        Assert.True(plugin.HasEditor, "the worker should report the plugin has an editor.");

        Assert.True(plugin.OpenEditor("MidiSharp sandbox editor test"), "the worker should open the editor.");
        _out.WriteLine("editor opened in the worker process");

        // The worker is still alive and serving: audio keeps flowing while the editor is up.
        using var effect = new HostedEffect(plugin, Config);
        var buf = new float[Block * 2];
        Array.Fill(buf, 0.3f);
        effect.Process(buf);
        Assert.False(plugin.IsDead, "the worker should still be alive with the editor open.");
        Assert.True(Math.Abs(buf[0] - 0.3f) < 1e-6, "the passthrough plugin should still process audio.");

        plugin.CloseEditor();
        Assert.False(plugin.IsDead);
    }

    [Fact]
    public void A_hung_plugin_is_killed_by_the_watchdog_and_recovers()
    {
        string? worker = WorkerDll();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");
        var f = new ClapFormat();
        PluginDescriptor? hang = f.Scan(f.DefaultSearchPaths).FirstOrDefault(p => p.Id == "midisharp.test.hang");
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
        string? worker = WorkerDll();
        PluginDescriptor? desc = GainDescriptor();
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

    [Fact]
    public void Host_and_worker_can_map_the_same_shared_block_at_once()
    {
        // Regression (Windows): the sandbox shares one backing file between the host process and the worker.
        // Both map it read-write at the same time. The string-path CreateFromFile overload opens with
        // FileShare.Read, so the worker's read-write open failed with "being used by another process".
        // OpenSharedBlock opens through a FileShare.ReadWrite stream, so the two maps coexist — and back the
        // SAME memory. Pure unit test: no worker process or plugin fixture, so it always runs.
        long size = SandboxProtocol.SharedSize(Block);
        string path = Path.Combine(Path.GetTempPath(), "midisharp-sbxtest-" + Guid.NewGuid().ToString("N") + ".bin");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.SetLength(size);
        try
        {
            using MemoryMappedFile host = SandboxProtocol.OpenSharedBlock(path, size);     // host holds it for the session
            using MemoryMappedFile worker = SandboxProtocol.OpenSharedBlock(path, size);   // the worker opens the same file

            using MemoryMappedViewAccessor hv = host.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            using MemoryMappedViewAccessor wv = worker.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            wv.Write(0, 0x12345678);
            Assert.Equal(0x12345678, hv.ReadInt32(0));   // a write through one map is visible through the other
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void A_failed_start_maps_the_block_then_cleans_up_without_leaking_a_temp_file()
    {
        // End-to-end across the real process boundary with a bogus plugin. The worker maps the shared block
        // (past the old Windows file-sharing crash at Program.cs:89) and only THEN fails to load the plugin,
        // so the host throws "failed to load plugin" — not "exited or hung during startup" (the symptom when
        // the worker died at the map). The throwing constructor must also clean up: no orphaned temp .bin.
        string? worker = WorkerDll();
        Assert.SkipWhen(worker == null, "sandbox worker not built.");

        string TempBins() => string.Join("|", Directory.GetFiles(Path.GetTempPath(), "midisharp-sbx-*.bin").OrderBy(s => s));
        var before = new HashSet<string>(TempBins().Split('|', StringSplitOptions.RemoveEmptyEntries));

        var bogus = new PluginDescriptor("CLAP", "does.not.exist", "Bogus", "", false,
            Path.Combine(Path.GetTempPath(), "midisharp-no-such-" + Guid.NewGuid().ToString("N") + ".clap"));

        var ex = Assert.Throws<InvalidOperationException>(() => new SandboxedPlugin(bogus, worker!, Config));
        _out.WriteLine("failed-start result: " + ex.Message);
        Assert.Contains("failed to load plugin", ex.Message);          // worker cleared the shared-block map
        Assert.DoesNotContain("exited or hung during startup", ex.Message);

        string[] leaked = TempBins().Split('|', StringSplitOptions.RemoveEmptyEntries).Where(f => !before.Contains(f)).ToArray();
        Assert.Empty(leaked);                                          // the throwing constructor deleted its temp file
    }
}
