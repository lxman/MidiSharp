using System;

namespace MidiSharp.Synth;

/// <summary>
/// Stereo chorus built from a small number of delay-line voices whose delay
/// time is modulated by a triangle LFO. Each voice has a different LFO phase,
/// and the L/R channels read taps at offset phases for stereo width.
/// </summary>
public sealed class Chorus
{
    private readonly int _sampleRate;
    private readonly float[] _delayBuf;
    private int _writeIdx;
    private readonly int _bufSize;

    private readonly int _voiceCount;
    private readonly float[] _voicePhase;       // 0..1
    private readonly float _phaseIncrement;     // per sample
    private readonly float _depthSamples;       // peak deviation in samples
    private readonly float _baseDelaySamples;   // centre of the modulated delay
    private float _level = 1f;

    /// <summary>Wet level mixed into the output, 0–1.</summary>
    public float Level
    {
        get => _level;
        set => _level = Math.Clamp(value, 0f, 4f);
    }

    /// <summary>
    /// Creates a stereo chorus with the given parameters.
    /// Defaults are tuned to roughly match the SF2 / EMU8k "default chorus" preset.
    /// </summary>
    /// <param name="sampleRate">Output sample rate.</param>
    /// <param name="voices">Number of internal voices (3 is typical).</param>
    /// <param name="rateHz">LFO rate in Hz.</param>
    /// <param name="depthMs">LFO peak deviation in milliseconds.</param>
    /// <param name="baseDelayMs">Centre delay in milliseconds.</param>
    public Chorus(
        int sampleRate = 44100,
        int voices = 3,
        float rateHz = 0.3f,
        float depthMs = 8f,
        float baseDelayMs = 20f)
    {
        _sampleRate = sampleRate;
        _voiceCount = voices;
        _voicePhase = new float[voices];
        for (var i = 0; i < voices; i++)
            _voicePhase[i] = (float)i / voices;

        _phaseIncrement = rateHz / sampleRate;
        _depthSamples = depthMs * 0.001f * sampleRate;
        _baseDelaySamples = baseDelayMs * 0.001f * sampleRate;

        // Buffer needs to hold the max possible read offset plus a little headroom.
        _bufSize = (int)(_baseDelaySamples + _depthSamples + 4) * 2;
        _delayBuf = new float[_bufSize];
    }

    public void Reset()
    {
        Array.Clear(_delayBuf, 0, _delayBuf.Length);
        _writeIdx = 0;
        for (var i = 0; i < _voiceCount; i++)
            _voicePhase[i] = (float)i / _voiceCount;
    }

    /// <summary>
    /// Reads a mono send bus and mixes the stereo chorused output into the L/R
    /// buffers. Input is unchanged; output spans are added to.
    /// </summary>
    public void Process(ReadOnlySpan<float> sendMono, Span<float> outL, Span<float> outR)
    {
        for (var i = 0; i < sendMono.Length; i++)
        {
            float input = sendMono[i];

            // Write input to the delay line.
            _delayBuf[_writeIdx] = input;

            // Sum N voice taps, each with its own LFO-modulated delay time.
            // Left and right channels take alternating voices for stereo decorrelation.
            float sumL = 0f, sumR = 0f;
            for (var v = 0; v < _voiceCount; v++)
            {
                // Triangle wave from saw via abs() — cheaper than sin() and indistinguishable here.
                float saw = _voicePhase[v] * 2f - 1f;
                float tri = 1f - Math.Abs(saw) * 2f;          // -1..+1 triangle
                float delay = _baseDelaySamples + tri * _depthSamples;
                float tapPos = _writeIdx - delay;
                while (tapPos < 0) tapPos += _bufSize;

                var idx0 = (int)tapPos;
                float frac = tapPos - idx0;
                // Defensive: clamp index after the cast. tapPos can equal _bufSize when
                // the value is a hair above _bufSize-1 due to fp; (int) truncates that
                // to _bufSize, which is out of bounds. Belt + suspenders.
                if (idx0 >= _bufSize) idx0 = _bufSize - 1;
                else if (idx0 < 0) idx0 = 0;
                int idx1 = idx0 + 1;
                if (idx1 >= _bufSize) idx1 = 0;
                float s = _delayBuf[idx0] + frac * (_delayBuf[idx1] - _delayBuf[idx0]);

                // Even voices → L, odd voices → R. (Voices 0 and 2 go L, 1 goes R for 3-voice.)
                if ((v & 1) == 0) sumL += s;
                else sumR += s;

                _voicePhase[v] += _phaseIncrement;
                if (_voicePhase[v] >= 1f) _voicePhase[v] -= 1f;
            }

            // Compensate so equal-voice distribution doesn't shift the apparent loudness.
            float scaleL = 2f / _voiceCount;
            float scaleR = 2f / _voiceCount;

            outL[i] += sumL * scaleL * _level;
            outR[i] += sumR * scaleR * _level;

            if (++_writeIdx >= _bufSize) _writeIdx = 0;
        }
    }
}
