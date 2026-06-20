using System.Collections.Generic;
using System.Linq;

namespace MidiSharp.Hosting;

/// <summary>
/// A plugin format the host can discover and load (LADSPA, CLAP, VST2, VST3, …). One implementation per
/// format owns all of that format's native interop; the format-agnostic core knows only this interface.
/// </summary>
public interface IPluginFormat
{
    /// <summary>Format name, used in <see cref="PluginDescriptor.Format"/> and the UI (e.g. "LADSPA").</summary>
    string Name { get; }

    /// <summary>
    /// The default per-OS directories this format installs into (e.g. <c>/usr/lib/ladspa</c>,
    /// <c>%COMMONPROGRAMFILES%\CLAP</c>). Callers may add their own paths.
    /// </summary>
    IEnumerable<string> DefaultSearchPaths { get; }

    /// <summary>
    /// The candidate plugin files/bundles under <paramref name="searchPaths"/>, in a stable (sorted)
    /// order. Touches no native code — just the filesystem — so it can't crash. Pair with
    /// <see cref="ScanFile"/> for crash-resilient, resumable scanning.
    /// </summary>
    IEnumerable<string> EnumerateFiles(IEnumerable<string> searchPaths);

    /// <summary>
    /// Scan ONE file/bundle returned by <see cref="EnumerateFiles"/> for its plugins. This loads native
    /// code (the plugin's entry/factory) and so may crash on a broken plugin — callers that need
    /// resilience run it out-of-process per file.
    /// </summary>
    IEnumerable<PluginDescriptor> ScanFile(string file);

    /// <summary>
    /// Discover every plugin reachable under <paramref name="searchPaths"/> (enumerate + scan each file).
    /// Cheap metadata only — does not instantiate or activate any plugin. Conventionally
    /// <c>EnumerateFiles(searchPaths).SelectMany(ScanFile)</c>.
    /// </summary>
    IEnumerable<PluginDescriptor> Scan(IEnumerable<string> searchPaths);

    /// <summary>
    /// Instantiate a plugin discovered by <see cref="Scan"/>. The returned instance is inactive; call
    /// <see cref="IHostedPlugin.Activate"/> before processing.
    /// </summary>
    IHostedPlugin Load(PluginDescriptor descriptor, AudioConfig config);
}
