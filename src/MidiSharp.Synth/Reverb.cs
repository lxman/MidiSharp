using System;

namespace MidiSharp.Synth;

/// <summary>
/// 8-line Feedback Delay Network reverb (Jot/Chaigne 1991), implemented clean-room
/// from the public algorithmic description in Smith's "Physical Audio Signal
/// Processing" textbook. Replaces the earlier Schroeder/Freeverb topology, which
/// is fundamentally low-pass-dominant and can't produce the HF-rich wash typical
/// SF2 patches expect.
///
/// Architecture:
///   input ──┬──► delay₀ ──► LPF₀ ──┐
///           ├──► delay₁ ──► LPF₁ ──┤
///           │      ...             │     8×8         ┌──► gain₀ ──┐
///           ├──► delay₇ ──► LPF₇ ──┤  Hadamard  ──►  │   ...      ├──► output mix → L, R
///                                  │   matrix        └──► gain₇ ──┘
///                                  └────── feedback ──────┘
///
/// The Hadamard matrix is orthogonal, so the loop is unconditionally stable
/// whenever feedback gains are &lt; 1. The 8-point Walsh-Hadamard transform takes
/// only 24 adds/subs (no multiplies) per sample via the butterfly algorithm.
/// </summary>
public sealed class Reverb
{
    // 8 coprime-ish delay lengths in samples @ 44.1 kHz, scaled to actual rate.
    private static readonly int[] DelayLensRef =
        [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];

    private const int N = 8;                       // number of delay lines
    private readonly float _hadamardScale = 1f / MathF.Sqrt(N);

    private readonly int _sampleRate;
    private readonly float[][] _delays;
    private readonly int[] _delaySize;
    private readonly int[] _writeIdx;
    private readonly float[] _lpfState;            // one per line — feedback LPF memory
    private readonly float[] _loopGain;            // per-line feedback attenuation
    private readonly float[] _tmp;                 // 8-element scratch for matrix mix

    private float _roomSize = 0.5f;
    private float _damp = 0.2f;
    private float _wet = 0.9f;
    private float _width = 1.0f;

    // Output mix coefficients: alternating signs give stereo decorrelation. With width=0
    // both channels read the same combination (mono); width=1 reads orthogonal mixes.
    private static readonly float[] OutCoeffL = [+1, -1, +1, -1, +1, -1, +1, -1];
    private static readonly float[] OutCoeffR = [+1, +1, -1, -1, +1, +1, -1, -1];

    /// <summary>Room size 0–1. Larger = longer reverb tail (T60 ~0.7 s to ~10 s).</summary>
    public float RoomSize
    {
        get => _roomSize;
        set { _roomSize = Math.Clamp(value, 0f, 1f); UpdateLoopGains(); }
    }

    /// <summary>High-frequency damping 0–1. 0 = bright; 1 = dark.</summary>
    public float Damp
    {
        get => _damp;
        set => _damp = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Wet level mixed into the output (0 = silent; 1 = nominal; &gt; 1 amplifies).</summary>
    public float Wet
    {
        get => _wet;
        set => _wet = Math.Max(0f, value);
    }

    /// <summary>Stereo width 0–1.</summary>
    public float Width
    {
        get => _width;
        set => _width = Math.Clamp(value, 0f, 1f);
    }

    public Reverb(int sampleRate = 44100)
    {
        _sampleRate = sampleRate;
        var rateScale = sampleRate / 44100f;
        _delays = new float[N][];
        _delaySize = new int[N];
        _writeIdx = new int[N];
        _lpfState = new float[N];
        _loopGain = new float[N];
        _tmp = new float[N];
        for (var i = 0; i < N; i++)
        {
            var size = Math.Max(1, (int)(DelayLensRef[i] * rateScale));
            _delaySize[i] = size;
            _delays[i] = new float[size];
        }
        UpdateLoopGains();
    }

    public void Reset()
    {
        for (var i = 0; i < N; i++)
        {
            Array.Clear(_delays[i], 0, _delays[i].Length);
            _lpfState[i] = 0f;
            _writeIdx[i] = 0;
        }
    }

    /// <summary>
    /// Reads a mono send bus; mixes the stereo wet output into the L/R buffers.
    /// Input is unchanged; output spans are added to (not overwritten).
    /// </summary>
    public void Process(ReadOnlySpan<float> sendMono, Span<float> outL, Span<float> outR)
    {
        if (sendMono.Length == 0) return;

        // Pre-compute width-based output mixing. width=0 → both channels read identical
        // mixture (mono); width=1 → channels read independent linear combinations.
        var widthMix = 0.5f * (1f - _width);   // amount of "other" channel bled in
        var mainMix  = 1f - widthMix;

        // One-pole LPF coefficient. damp=0 → a=0 (pass-through); damp=1 → a≈0.6 (heavy LP).
        // Lerp instead of hard cutoff math so the parameter feels smooth.
        var lpfA = _damp * 0.6f;
        var lpfB = 1f - lpfA;

        // Wet scale: split wet between "main" and "cross" channels for the stereo image.
        var wetMain = _wet * mainMix;
        var wetCross = _wet * widthMix;

        // Input scale: divide by N so summed input doesn't explode the loop gain.
        // (Energy preservation expects the input split N ways.)
        const float inputScale = 1f / N;

        for (var s = 0; s < sendMono.Length; s++)
        {
            var input = sendMono[s] * inputScale;

            // Read tap from each delay line, apply LPF, apply loop gain.
            // Result lives in _tmp[] = the 8 "delay outputs" feeding back through the matrix.
            for (var i = 0; i < N; i++)
            {
                var readPos = _writeIdx[i] + 1;          // oldest sample = just past write
                if (readPos >= _delaySize[i]) readPos = 0;
                var raw = _delays[i][readPos];
                // One-pole LPF on the feedback path (the "damp" control).
                var filt = lpfB * raw + lpfA * _lpfState[i];
                _lpfState[i] = filt;
                _tmp[i] = filt * _loopGain[i];
            }

            // Compose output BEFORE the matrix mix (so we hear the pre-mix taps directly).
            // This gives a more focused stereo image than reading post-matrix.
            float sumL = 0f, sumR = 0f;
            for (var i = 0; i < N; i++)
            {
                sumL += _tmp[i] * OutCoeffL[i];
                sumR += _tmp[i] * OutCoeffR[i];
            }
            // Normalise the output sum (8 taps × ±1) — empirical scale to keep wet
            // perceptually similar to Wet=0.9 in the prior Freeverb implementation.
            const float outNorm = 1f / 8f;
            outL[s] += (sumL * wetMain + sumR * wetCross) * outNorm;
            outR[s] += (sumR * wetMain + sumL * wetCross) * outNorm;

            // Walsh-Hadamard butterfly mix on _tmp[] in place.
            // After this _tmp[] contains the matrix-mixed feedback that goes back into the delays.
            WalshHadamard8(_tmp);

            // Write input + feedback to each delay line; advance write pointer.
            for (var i = 0; i < N; i++)
            {
                _writeIdx[i]++;
                if (_writeIdx[i] >= _delaySize[i]) _writeIdx[i] = 0;
                _delays[i][_writeIdx[i]] = input + _tmp[i] * _hadamardScale;
            }
        }
    }

    /// <summary>
    /// In-place 8-point Walsh-Hadamard transform via the butterfly algorithm.
    /// 24 add/sub ops, no multiplies. The √(1/8) normalisation is applied later
    /// at the feedback write site, since we want the natural integer-coefficient
    /// mix here.
    /// </summary>
    private static void WalshHadamard8(float[] x)
    {
        // Stride 4
        float a, b;
        a = x[0]; b = x[4]; x[0] = a + b; x[4] = a - b;
        a = x[1]; b = x[5]; x[1] = a + b; x[5] = a - b;
        a = x[2]; b = x[6]; x[2] = a + b; x[6] = a - b;
        a = x[3]; b = x[7]; x[3] = a + b; x[7] = a - b;
        // Stride 2
        a = x[0]; b = x[2]; x[0] = a + b; x[2] = a - b;
        a = x[1]; b = x[3]; x[1] = a + b; x[3] = a - b;
        a = x[4]; b = x[6]; x[4] = a + b; x[6] = a - b;
        a = x[5]; b = x[7]; x[5] = a + b; x[7] = a - b;
        // Stride 1
        a = x[0]; b = x[1]; x[0] = a + b; x[1] = a - b;
        a = x[2]; b = x[3]; x[2] = a + b; x[3] = a - b;
        a = x[4]; b = x[5]; x[4] = a + b; x[5] = a - b;
        a = x[6]; b = x[7]; x[6] = a + b; x[7] = a - b;
    }

    /// <summary>
    /// Derive per-line loop gains from RoomSize. Each gain is chosen so the line's
    /// signal decays by 60 dB over the target T60, taking the line's own delay
    /// length into account (Jot's design: shorter delays decay slower per pass to
    /// preserve overall T60 across all lines).
    ///
    /// RoomSize 0 → T60 ≈ 0.5 s; RoomSize 1 → T60 ≈ 8 s (logarithmic mapping).
    /// </summary>
    private void UpdateLoopGains()
    {
        // Exponential mapping: T60 = 0.5 * 16^roomsize gives 0.5 .. 8 s
        var t60 = 0.5f * MathF.Pow(16f, _roomSize);
        for (var i = 0; i < N; i++)
        {
            var delaySeconds = (float)_delaySize[i] / _sampleRate;
            // g such that g^(t60 / delaySeconds) = 10^-3  (= -60 dB)
            // → g = 10^(-3 * delaySeconds / t60)
            _loopGain[i] = MathF.Pow(10f, -3f * delaySeconds / t60);
        }
    }
}
