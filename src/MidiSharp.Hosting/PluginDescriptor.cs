namespace MidiSharp.Hosting;

/// <summary>
/// Identifies one discoverable plugin, independent of format. Produced by <see cref="IPluginFormat.Scan"/>
/// and passed back to <see cref="IPluginFormat.Load"/> to instantiate it.
/// </summary>
/// <param name="Format">The owning format's name, e.g. "LADSPA", "CLAP", "VST2".</param>
/// <param name="Id">Stable per-format identity (LADSPA UniqueID, CLAP id, VST2 uniqueID) as a string.</param>
/// <param name="Name">Human-readable plugin name for the UI.</param>
/// <param name="Vendor">Plugin maker, when the format exposes one (else empty).</param>
/// <param name="IsInstrument">True for sound sources (synths), false for effects.</param>
/// <param name="Path">The container file/bundle on disk the plugin lives in.</param>
public sealed record PluginDescriptor(
    string Format,
    string Id,
    string Name,
    string Vendor,
    bool IsInstrument,
    string Path);
