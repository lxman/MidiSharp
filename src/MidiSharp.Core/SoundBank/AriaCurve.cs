using System;

namespace MidiSharp.SoundBank;

/// <summary>
/// ARIA built-in CC response curves (SFZ <c>*_curvecc</c>), indices 0–6 — linear ramps (0–3) and
/// quadratic / square-root shapes (4–6). Custom &lt;curve&gt; definitions aren't modelled and fall back
/// to linear here. Matches sfizz's predefined curves.
/// </summary>
public static class AriaCurve
{
    /// <summary>Evaluates curve <paramref name="index"/> at normalised input x∈[0,1].</summary>
    public static double Eval(int index, double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        return index switch
        {
            0 => x,                  // 0 → 1 linear
            1 => 2 * x - 1,          // -1 → +1
            2 => 1 - x,              // 1 → 0
            3 => 1 - 2 * x,          // +1 → -1
            4 => x * x,              // concave (slow rise)
            5 => Math.Sqrt(x),       // convex (fast rise)
            6 => Math.Sqrt(1 - x),   // 1 → 0 convex
            _ => x,
        };
    }
}
