using System.Collections.Generic;

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
    /// Discover every plugin reachable under <paramref name="searchPaths"/>. Cheap metadata only — does
    /// not instantiate or activate anything.
    /// </summary>
    IEnumerable<PluginDescriptor> Scan(IEnumerable<string> searchPaths);

    /// <summary>
    /// Instantiate a plugin discovered by <see cref="Scan"/>. The returned instance is inactive; call
    /// <see cref="IHostedPlugin.Activate"/> before processing.
    /// </summary>
    IHostedPlugin Load(PluginDescriptor descriptor, AudioConfig config);
}
