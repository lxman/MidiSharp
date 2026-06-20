using System.IO;
using MidiSharp.Hosting;
using MidiSharp.Hosting.Clap;
using MidiSharp.Hosting.Ladspa;
using MidiSharp.Hosting.Sandbox;
using MidiSharp.Hosting.Vst2;
using MidiSharp.Hosting.Vst3;

namespace MidiSharp.Server;

// Discovered-plugin metadata for the picker.
public sealed record PluginDescriptorDto(string Format, string Id, string Name, string Vendor, bool IsInstrument);
// One automatable parameter, ranges in the plugin's real units plus the host-normalized default.
public sealed record PluginParamDto(int Index, string Name, string Label, double Min, double Max, double Default, double DefaultNormalized, bool IsStepped);
// A plugin's full param list, fetched when it's added to a rack so the UI can render its knobs.
public sealed record PluginInfoDto(string Format, string Id, string Name, bool IsInstrument, PluginParamDto[] Params);

/// <summary>
/// The server's plugin host: owns the cross-format <see cref="PluginRegistry"/> (CLAP + LADSPA), scans
/// once at construction, and loads plugins on demand. Loading instantiates native code, so it's done off
/// the audio thread (rack (re)configure / param-info fetch).
/// </summary>
public sealed class PluginHost
{
    // CLAP effects can ask for blocks up to this many frames; HostedEffect chunks anything larger, and the
    // real audio callback is far smaller, so this is just the activation ceiling.
    public const int MaxBlockFrames = 4096;

    private readonly PluginRegistry _registry = new PluginRegistry()
        .Register(new ClapFormat())
        .Register(new Vst3Format())
        .Register(new Vst2Format())
        .Register(new LadspaFormat());

    private static readonly string[] Formats = ["CLAP", "VST3", "VST2", "LADSPA"];

    private readonly int _sampleRate;
    private readonly string? _workerDll;
    private readonly bool _sandbox;
    private List<PluginDescriptor> _plugins = [];

    public PluginHost(int sampleRate)
    {
        _sampleRate = sampleRate;
        // Sandbox plugins out-of-process by default (a crashing plugin then can't take down the server),
        // when the worker is locatable and not explicitly disabled. Falls back to in-process otherwise.
        _workerDll = ResolveWorker();
        _sandbox = _workerDll != null && Environment.GetEnvironmentVariable("MIDISHARP_SANDBOX") != "0";
        Console.WriteLine(_sandbox
            ? $"  Plugin sandbox:  ON  (worker: {_workerDll})"
            : "  Plugin sandbox:  OFF (in-process; set the worker or unset MIDISHARP_SANDBOX=0)");
        try { Rescan(); } catch { /* a broken format dir shouldn't sink startup */ }
    }

    public AudioConfig Config => new(_sampleRate, MaxBlockFrames, ChannelCount: 2);

    /// <summary>True when plugins are hosted out-of-process.</summary>
    public bool Sandboxed => _sandbox;

    /// <summary>Re-discover plugins. When sandboxed, the scan itself runs in worker processes (one per
    /// format), so a plugin that crashes during discovery can't take the server down.</summary>
    public void Rescan()
    {
        if (_sandbox && _workerDll != null)
        {
            _plugins = SandboxScanner.ScanAll(Formats, _workerDll);
        }
        else
        {
            _registry.Rescan();
            _plugins = _registry.Plugins.ToList();
        }
    }

    public IReadOnlyList<PluginDescriptorDto> List() => _plugins
        .Select(p => new PluginDescriptorDto(p.Format, p.Id, p.Name, p.Vendor, p.IsInstrument))
        .ToList();

    /// <summary>Load a plugin transiently to read its parameters, then dispose it. Sandboxed when on, so a
    /// plugin that crashes on load can't take the server down — it surfaces as a failed info fetch.</summary>
    public PluginInfoDto? GetInfo(string format, string id)
    {
        var desc = Find(format, id);
        if (desc == null) return null;
        using var plugin = Instantiate(desc);
        var pars = plugin.Parameters
            .Select(p => new PluginParamDto(p.Index, p.Name, p.Label, p.MinValue, p.MaxValue,
                p.DefaultValue, p.Normalize(p.DefaultValue), p.IsStepped))
            .ToArray();
        return new PluginInfoDto(desc.Format, desc.Id, plugin.Descriptor.Name, plugin.IsInstrument, pars);
    }

    /// <summary>Instantiate a plugin for live use; the caller owns and disposes it.</summary>
    public IHostedPlugin Load(string format, string id)
    {
        var desc = Find(format, id) ?? throw new KeyNotFoundException($"Plugin {format}:{id} not found.");
        return Instantiate(desc);
    }

    // Out-of-process (a worker per plugin) when the sandbox is on; in-process otherwise.
    private IHostedPlugin Instantiate(PluginDescriptor desc) =>
        _sandbox ? new SandboxedPlugin(desc, _workerDll!, Config) : _registry.Load(desc, Config);

    private PluginDescriptor? Find(string format, string id) =>
        _plugins.FirstOrDefault(p => p.Format == format && p.Id == id);

    // Locate MidiSharp.Hosting.Worker.dll: an explicit override, then alongside the server (published
    // layout), then the worker's own build output (dev layout: samples/MidiSharp.Server → src/…Worker).
    private static string? ResolveWorker()
    {
        var env = Environment.GetEnvironmentVariable("MIDISHARP_WORKER");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        var baseDir = AppContext.BaseDirectory;
        var alongside = Path.Combine(baseDir, "MidiSharp.Hosting.Worker.dll");
        if (File.Exists(alongside)) return alongside;

        var dev = baseDir.Replace(
            Path.Combine("samples", "MidiSharp.Server"),
            Path.Combine("src", "MidiSharp.Hosting.Worker"));
        var devDll = Path.Combine(dev, "MidiSharp.Hosting.Worker.dll");
        return File.Exists(devDll) ? devDll : null;
    }
}
