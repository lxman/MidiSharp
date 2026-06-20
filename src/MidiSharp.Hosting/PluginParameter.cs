namespace MidiSharp.Hosting;

/// <summary>
/// Metadata for one automatable plugin parameter. The host UI works in a normalized 0..1 space (the
/// generic-knob model the web UI already builds strips from); a format adapter maps that to the
/// plugin's real range. The label/min/max/default come from the plugin so the UI can show meaningful
/// values without a plugin GUI.
/// </summary>
public sealed class PluginParameter
{
    public PluginParameter(int index, string name, string label,
        double minValue, double maxValue, double defaultValue,
        bool isStepped = false, bool isLogarithmic = false)
    {
        Index = index;
        Name = name;
        Label = label;
        MinValue = minValue;
        MaxValue = maxValue;
        DefaultValue = defaultValue;
        IsStepped = isStepped;
        IsLogarithmic = isLogarithmic;
    }

    /// <summary>Index the host uses to get/set this parameter on its plugin.</summary>
    public int Index { get; }

    /// <summary>Display name (e.g. "Cutoff").</summary>
    public string Name { get; }

    /// <summary>Unit label (e.g. "Hz", "dB"); empty when the format gives none.</summary>
    public string Label { get; }

    public double MinValue { get; }
    public double MaxValue { get; }
    public double DefaultValue { get; }

    /// <summary>Integer/toggle-valued parameter (quantize the UI control).</summary>
    public bool IsStepped { get; }

    /// <summary>Parameter is perceptually logarithmic (e.g. a frequency); the UI may use a log taper.</summary>
    public bool IsLogarithmic { get; }

    /// <summary>Map a real plugin value into the host's 0..1 space (linear; log taper is a UI concern).</summary>
    public double Normalize(double value)
        => MaxValue > MinValue ? Clamp01((value - MinValue) / (MaxValue - MinValue)) : 0.0;

    /// <summary>Map a 0..1 host value back to the plugin's real range.</summary>
    public double Denormalize(double normalized)
        => MinValue + Clamp01(normalized) * (MaxValue - MinValue);

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
