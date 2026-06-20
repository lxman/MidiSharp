using MidiSharp.Hosting;
using MidiSharp.Hosting.Clap;
using MidiSharp.Hosting.Ladspa;
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

    private readonly int _sampleRate;

    public PluginHost(int sampleRate)
    {
        _sampleRate = sampleRate;
        try { _registry.Rescan(); } catch { /* a broken format dir shouldn't sink startup */ }
    }

    public AudioConfig Config => new(_sampleRate, MaxBlockFrames, ChannelCount: 2);

    public void Rescan() => _registry.Rescan();

    public IReadOnlyList<PluginDescriptorDto> List() => _registry.Plugins
        .Select(p => new PluginDescriptorDto(p.Format, p.Id, p.Name, p.Vendor, p.IsInstrument))
        .ToList();

    /// <summary>Load a plugin transiently to read its parameters, then dispose it.</summary>
    public PluginInfoDto? GetInfo(string format, string id)
    {
        var desc = Find(format, id);
        if (desc == null) return null;
        using var plugin = _registry.Load(desc, Config);
        var pars = plugin.Parameters
            .Select(p => new PluginParamDto(p.Index, p.Name, p.Label, p.MinValue, p.MaxValue,
                p.DefaultValue, p.Normalize(p.DefaultValue), p.IsStepped))
            .ToArray();
        return new PluginInfoDto(desc.Format, desc.Id, desc.Name, desc.IsInstrument, pars);
    }

    /// <summary>Instantiate a plugin for live use; the caller owns and disposes it.</summary>
    public IHostedPlugin Load(string format, string id)
    {
        var desc = Find(format, id) ?? throw new KeyNotFoundException($"Plugin {format}:{id} not found.");
        return _registry.Load(desc, Config);
    }

    private PluginDescriptor? Find(string format, string id) =>
        _registry.Plugins.FirstOrDefault(p => p.Format == format && p.Id == id);
}
