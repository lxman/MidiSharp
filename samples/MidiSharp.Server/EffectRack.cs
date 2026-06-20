using MidiSharp.Dsp;
using MidiSharp.Hosting;
using MidiSharp.Synth;

namespace MidiSharp.Server;

/// <summary>
/// A configurable effect rack: built-in EQ + limiter + output-gain instances plus any number of hosted
/// plugins (CLAP/LADSPA), all behind a <see cref="ProcessorChain"/> and (re)built in order from an
/// <see cref="EffectDto"/> list. Built-in instances persist across reconfigures so DSP state survives a
/// reorder; hosted plugins persist by <c>InstanceId</c> so a parameter tweak reuses the loaded instance
/// rather than reloading native code. Implements <see cref="IInstrumentInsert"/> so the same class serves
/// the master bus and per-instrument inserts.
/// </summary>
internal sealed class EffectRack : IInstrumentInsert, IDisposable
{
    private readonly ParametricEq _eq;
    private readonly LimiterProcessor _limiter;
    private readonly GainProcessor _trailingGain = new();
    private readonly ProcessorChain _chain = new();

    private readonly PluginHost? _pluginHost;
    private readonly Dictionary<string, HostedEffect> _plugins = new();   // keyed by InstanceId
    private readonly List<HostedEffect> _pendingDispose = [];             // freed one Configure later

    public EffectRack(int sampleRate, PluginHost? pluginHost = null)
    {
        _pluginHost = pluginHost;
        _eq = new ParametricEq(sampleRate);
        _limiter = new LimiterProcessor(sampleRate) { Enabled = false };
    }

    public bool IsEmpty => _chain.Processors.Count == 0;

    /// <summary>Rebuilds the chain from the effect list, in order, then a final output-gain fader
    /// (<paramref name="trailingGainDb"/>, master only). Disabled effects keep their slot but stay out
    /// of the signal path. Hosted plugins not present this pass are disposed (deferred one cycle so the
    /// audio thread can't be mid-Process on a freed instance).</summary>
    public void Configure(IReadOnlyList<EffectDto>? effects, double trailingGainDb = 0)
    {
        // Reclaim plugins removed on the previous Configure — the chain has long since stopped using them.
        foreach (var dead in _pendingDispose) dead.Dispose();
        _pendingDispose.Clear();

        var ordered = new List<IAudioProcessor>();
        var live = new HashSet<string>();
        if (effects != null)
            foreach (var e in effects)
            {
                if (!e.Enabled) continue;
                switch (e.Type?.ToLowerInvariant())
                {
                    case "eq":
                        _eq.SetBands(e.EqBands is { Length: > 0 } b ? Array.ConvertAll(b, ToEqSpec) : []);
                        ordered.Add(_eq);
                        break;
                    case "limiter":
                        _limiter.Enabled = true;
                        _limiter.CeilingDb = e.CeilingDb;
                        _limiter.ReleaseMs = e.ReleaseMs > 0 ? e.ReleaseMs : 100.0;
                        ordered.Add(_limiter);
                        break;
                    case "plugin":
                        var he = ResolvePlugin(e);
                        if (he != null) { ordered.Add(he); live.Add(KeyOf(e)); }
                        break;
                }
            }

        // Plugins no longer in the list: pull them from the lookup now, dispose next Configure.
        foreach (var key in _plugins.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _pendingDispose.Add(_plugins[key]);
            _plugins.Remove(key);
        }

        if (trailingGainDb != 0)
        {
            _trailingGain.GainDb = trailingGainDb;
            ordered.Add(_trailingGain);
        }
        _chain.SetAll(ordered);
    }

    // Reuse the loaded instance for this InstanceId if present (just push new params), else load it.
    private HostedEffect? ResolvePlugin(EffectDto e)
    {
        if (_pluginHost == null || string.IsNullOrEmpty(e.PluginFormat) || string.IsNullOrEmpty(e.PluginId))
            return null;

        var key = KeyOf(e);
        if (!_plugins.TryGetValue(key, out var he))
        {
            try
            {
                var plugin = _pluginHost.Load(e.PluginFormat, e.PluginId);
                he = new HostedEffect(plugin, _pluginHost.Config);
                _plugins[key] = he;
                if (!string.IsNullOrEmpty(e.PluginState))
                    plugin.LoadState(Convert.FromBase64String(e.PluginState));
            }
            catch { return null; }   // missing/incompatible plugin → skip the insert
        }

        if (e.PluginParams != null)
            for (var i = 0; i < e.PluginParams.Length && i < he.Plugin.Parameters.Count; i++)
                he.Plugin.SetParameter(i, e.PluginParams[i]);
        return he;
    }

    private static string KeyOf(EffectDto e) => e.InstanceId ?? $"{e.PluginFormat}:{e.PluginId}";

    /// <summary>Current opaque state of a loaded plugin insert (by InstanceId) as base64, or null when it
    /// has no instance or no state. Used to capture live plugin state into a saved setup.</summary>
    public string? GetPluginState(string instanceId)
    {
        if (!_plugins.TryGetValue(instanceId, out var he)) return null;
        var blob = he.Plugin.SaveState();
        return blob.Length > 0 ? Convert.ToBase64String(blob) : null;
    }

    /// <summary>The live hosted plugin for an InstanceId, or null if this rack doesn't hold it. Lets the
    /// server reach a loaded plugin (e.g. to open its editor) without exposing the rack's internals.</summary>
    public IHostedPlugin? FindPlugin(string instanceId)
        => _plugins.TryGetValue(instanceId, out var he) ? he.Plugin : null;

    public void Process(Span<float> interleavedStereo) => _chain.Process(interleavedStereo);

    public void Reset() => _chain.Reset();

    public void Dispose()
    {
        foreach (var p in _pendingDispose) p.Dispose();
        _pendingDispose.Clear();
        foreach (var p in _plugins.Values) p.Dispose();
        _plugins.Clear();
    }

    private static EqBandSpec ToEqSpec(EqBandDto b)
    {
        var type = b.Type?.ToLowerInvariant() switch
        {
            "lowshelf" => BiquadType.LowShelf,
            "highshelf" => BiquadType.HighShelf,
            "lowpass" => BiquadType.LowPass,
            "highpass" => BiquadType.HighPass,
            "notch" => BiquadType.Notch,
            _ => BiquadType.Peaking,
        };
        return new EqBandSpec(type, b.FreqHz, b.Q > 0 ? b.Q : 0.707, b.GainDb);
    }
}
