using System;
using Xunit;

namespace MidiSharp.Synth.Tests;

/// <summary>
/// Step 3 of the synth-genericization plan adds domain-typed
/// <c>SetParameters</c> overloads to Envelope, LowFrequencyOscillator, and
/// LowPassFilter alongside the existing SF2-unit ones. These tests pin the
/// equivalence: feeding the same physical configuration via either API must
/// produce identical per-sample output. If a future change drifts the two
/// paths apart, the SF2 loader's translation will silently change voice
/// behavior — which is exactly the bug we want to catch.
/// </summary>
public class DomainTypedSetParametersTests
{
    private const int SampleRate = 44100;

    // ── Envelope ──────────────────────────────────────────────────────

    [Fact]
    public void Envelope_DomainTypedMatchesSf2Units_AcrossFullCycle()
    {
        // SF2 inputs: delay=-12000 (0 s), attack=-7973 (≈10 ms), hold=-12000 (0 s),
        // decay=-1000 (≈562 ms), sustain=600 cb (≈10^-3 linear), release=-2000 (≈316 ms).
        var sf2 = new Envelope(SampleRate);
        sf2.SetParameters(
            delayTimecents: -12000,
            attackTimecents: -7973,
            holdTimecents: -12000,
            decayTimecents: -1000,
            sustainCentibels: 600,
            releaseTimecents: -2000);

        var domain = new Envelope(SampleRate);
        domain.SetParameters(
            delaySeconds: 0.0,
            attackSeconds: Math.Pow(2.0, -7973 / 1200.0),
            holdSeconds: 0.0,
            decaySeconds: Math.Pow(2.0, -1000 / 1200.0),
            sustainLevel: Math.Pow(10.0, -600 / 200.0),
            releaseSeconds: Math.Pow(2.0, -2000 / 1200.0));

        sf2.Trigger();
        domain.Trigger();

        // Render through attack+decay+sustain.
        for (var i = 0; i < SampleRate; i++)
        {
            double a = sf2.Process();
            double b = domain.Process();
            Assert.Equal(a, b, precision: 12);
        }

        // Release and render the rest.
        sf2.Release();
        domain.Release();
        for (var i = 0; i < SampleRate; i++)
        {
            double a = sf2.Process();
            double b = domain.Process();
            Assert.Equal(a, b, precision: 12);
        }
    }

    [Fact]
    public void Envelope_ZeroSecondsSkipsPhase_SameAsMinusTwelveThousandTimecents()
    {
        // -12000 timecents is the SF2 "instantaneous" sentinel; 0 seconds in
        // the domain API should map to the same skipped-phase behavior.
        var sf2 = new Envelope(SampleRate);
        sf2.SetParameters(-12000, -12000, -12000, -12000, sustainCentibels: 0, -12000);
        sf2.Trigger();

        var domain = new Envelope(SampleRate);
        domain.SetParameters(
            delaySeconds: 0, attackSeconds: 0, holdSeconds: 0, decaySeconds: 0,
            sustainLevel: 1.0, releaseSeconds: 0);
        domain.Trigger();

        // Both should jump straight to sustain at full level after one Process.
        sf2.Process();
        domain.Process();
        Assert.Equal(sf2.Value, domain.Value, precision: 12);
        Assert.Equal(1.0, domain.Value);
        Assert.Equal(EnvelopeStage.Sustain, sf2.Stage);
        Assert.Equal(EnvelopeStage.Sustain, domain.Stage);
    }

    // ── LFO ───────────────────────────────────────────────────────────

    [Fact]
    public void Lfo_DomainTypedMatchesSf2Units_OverFullCycle()
    {
        // freqCents=1200 = one octave above 8.176 Hz = 16.352 Hz.
        // delay=-7973 timecents ≈ 10 ms.
        const short FreqCents = 1200;
        const short DelayTc = -7973;

        var sf2 = new LowFrequencyOscillator(SampleRate);
        sf2.SetParameters(DelayTc, FreqCents);

        var domain = new LowFrequencyOscillator(SampleRate);
        domain.SetParameters(
            delaySeconds: Math.Pow(2.0, DelayTc / 1200.0),
            frequencyHz: 8.176 * Math.Pow(2.0, FreqCents / 1200.0));

        sf2.Trigger();
        domain.Trigger();

        // Run two full cycles' worth of samples.
        var samples = (int)(SampleRate / 4.0); // ~250 ms at 44.1 kHz
        for (var i = 0; i < samples; i++)
        {
            double a = sf2.Process();
            double b = domain.Process();
            Assert.Equal(a, b, precision: 12);
        }
    }

    [Fact]
    public void Lfo_ZeroAbsoluteCentsEquals8Point176Hz()
    {
        var sf2 = new LowFrequencyOscillator(SampleRate);
        sf2.SetParameters(delayTimecents: -12000, freqCents: 0);

        var domain = new LowFrequencyOscillator(SampleRate);
        domain.SetParameters(delaySeconds: 0.0, frequencyHz: 8.176);

        sf2.Trigger();
        domain.Trigger();

        // Period at 8.176 Hz at 44.1 kHz ≈ 5394 samples; check a chunk that crosses zero.
        for (var i = 0; i < 6000; i++)
        {
            double a = sf2.Process();
            double b = domain.Process();
            Assert.Equal(a, b, precision: 12);
        }
    }

    // ── Filter ────────────────────────────────────────────────────────

    [Fact]
    public void Filter_DomainTypedMatchesSf2Units_OnWhiteNoise()
    {
        // cutoffCents=8400 ≈ 1058 Hz, resonance=200 cb = 20 dB → Q ≈ 10.
        const short CutoffCents = 8400;
        const short ResonanceCb = 200;

        var sf2 = new LowPassFilter(SampleRate);
        sf2.SetParameters(CutoffCents, ResonanceCb);

        var domain = new LowPassFilter(SampleRate);
        domain.SetParameters(
            cutoffHz: 8.176 * Math.Pow(2.0, CutoffCents / 1200.0),
            resonanceDb: ResonanceCb / 10.0);

        // Drive both with the same pseudo-random signal and confirm identical output.
        var rng = new Random(0xC0FFEE);
        for (var i = 0; i < 10_000; i++)
        {
            double sample = rng.NextDouble() * 2.0 - 1.0;
            double a = sf2.Process(sample);
            double b = domain.Process(sample);
            Assert.Equal(a, b, precision: 12);
        }
    }

    [Fact]
    public void Filter_HighCutoffDisablesFilterIdentically()
    {
        // 13500 cents ≈ 19914 Hz > Nyquist (clamped); filter should be disabled
        // and Process should pass input through unchanged via both APIs.
        var sf2 = new LowPassFilter(SampleRate);
        sf2.SetParameters(cutoffCents: 13500, resonanceCentibels: 0);
        Assert.False(sf2.Enabled);

        var domain = new LowPassFilter(SampleRate);
        domain.SetParameters(cutoffHz: 8.176 * Math.Pow(2.0, 13500 / 1200.0), resonanceDb: 0);
        Assert.False(domain.Enabled);

        // Both should pass any input through verbatim.
        Assert.Equal(0.42, sf2.Process(0.42));
        Assert.Equal(0.42, domain.Process(0.42));
    }
}
