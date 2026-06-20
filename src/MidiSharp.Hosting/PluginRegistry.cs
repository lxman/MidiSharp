using System.Collections.Generic;
using System.Linq;

namespace MidiSharp.Hosting;

/// <summary>
/// Aggregates the registered plugin formats and the plugins discovered across them. The host registers
/// one <see cref="IPluginFormat"/> per supported format, scans, then loads descriptors on demand. Scan
/// results are cached until <see cref="Rescan"/>.
/// </summary>
public sealed class PluginRegistry
{
    private readonly List<IPluginFormat> _formats = [];
    private readonly List<PluginDescriptor> _plugins = [];

    /// <summary>Register a format adapter. Returns this for chaining.</summary>
    public PluginRegistry Register(IPluginFormat format)
    {
        _formats.Add(format);
        return this;
    }

    public IReadOnlyList<IPluginFormat> Formats => _formats;

    /// <summary>All plugins found by the last <see cref="Rescan"/> (empty until one runs).</summary>
    public IReadOnlyList<PluginDescriptor> Plugins => _plugins;

    /// <summary>
    /// Re-discover plugins across every registered format, each over its own default search paths plus
    /// any <paramref name="extraPaths"/>. A format that throws while scanning is skipped, not fatal.
    /// </summary>
    public void Rescan(IEnumerable<string>? extraPaths = null)
    {
        var extra = extraPaths?.ToArray() ?? [];
        _plugins.Clear();
        foreach (var format in _formats)
        {
            try
            {
                var paths = format.DefaultSearchPaths.Concat(extra);
                _plugins.AddRange(format.Scan(paths));
            }
            catch
            {
                // A broken format adapter shouldn't sink discovery of the others.
            }
        }
    }

    /// <summary>Load a descriptor with the format that produced it.</summary>
    public IHostedPlugin Load(PluginDescriptor descriptor, AudioConfig config)
    {
        var format = _formats.FirstOrDefault(f => f.Name == descriptor.Format)
                     ?? throw new KeyNotFoundException($"No registered format named '{descriptor.Format}'.");
        return format.Load(descriptor, config);
    }
}
