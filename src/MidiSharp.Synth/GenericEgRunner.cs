using System;
using System.Collections.Generic;
using MidiSharp.SoundBank;

namespace MidiSharp.Synth;

/// <summary>
/// Per-voice runtime for one SFZ v2 flex envelope (<c>egN</c>): a sequence of timed level segments that
/// ramps from 0 through each stage and holds at the sustain stage. Exposes per-destination depths the
/// voice folds into the per-sample pitch / cutoff / attenuation, refreshed per block from live CCs.
/// Reused across notes to avoid per-NoteOn allocation.
/// </summary>
internal sealed class GenericEgRunner
{
    private int _sampleRate = 48000;
    private double[] _levels = [];
    private int[] _stageSamples = [];
    private int _stageCount;
    private int _sustainStage = -1;
    private int _stage;
    private int _sampleInStage;
    private double _prevLevel;
    private double _value;

    private double _basePitch, _baseCutoff, _baseVolume;
    private LfoCcDepth[]? _pitchCc, _cutoffCc, _volumeCc;

    public double PitchDepthCents { get; private set; }
    public double CutoffDepthCents { get; private set; }
    public double VolumeDepthDb { get; private set; }

    public void Configure(GenericEg eg, int sampleRate)
    {
        _sampleRate = sampleRate;
        var n = eg.Stages.Length;
        if (_levels.Length < n) { _levels = new double[n]; _stageSamples = new int[n]; }
        for (var i = 0; i < n; i++)
        {
            _levels[i] = eg.Stages[i].Level;
            _stageSamples[i] = eg.Stages[i].TimeSeconds > 0 ? (int)(eg.Stages[i].TimeSeconds * sampleRate) : 0;
        }
        _stageCount = n;
        _sustainStage = eg.SustainStage < 0 ? n - 1 : Math.Min(eg.SustainStage, n - 1);
        _stage = 0;
        _sampleInStage = 0;
        _prevLevel = 0.0;
        _value = 0.0;

        _basePitch = _baseCutoff = _baseVolume = 0;
        _pitchCc = _cutoffCc = _volumeCc = null;
        foreach (var t in eg.Targets)
        {
            switch (t.Destination)
            {
                case LfoDestination.Pitch:  _basePitch += t.Depth;  _pitchCc = Merge(_pitchCc, t.DepthCc); break;
                case LfoDestination.Cutoff: _baseCutoff += t.Depth; _cutoffCc = Merge(_cutoffCc, t.DepthCc); break;
                case LfoDestination.Volume: _baseVolume += t.Depth; _volumeCc = Merge(_volumeCc, t.DepthCc); break;
            }
        }
        PitchDepthCents = _basePitch;
        CutoffDepthCents = _baseCutoff;
        VolumeDepthDb = _baseVolume;
    }

    public void BeginBlock(ChannelState ch)
    {
        PitchDepthCents = _basePitch + SumCc(_pitchCc, ch);
        CutoffDepthCents = _baseCutoff + SumCc(_cutoffCc, ch);
        VolumeDepthDb = _baseVolume + SumCc(_volumeCc, ch);
    }

    /// <summary>Advances one sample through the envelope and returns its current level.</summary>
    public double Process()
    {
        if (_stageCount == 0 || _stage > _sustainStage) return _value;   // held at the sustain level

        var len = _stageSamples[_stage];
        var target = _levels[_stage];
        _value = len <= 0 ? target : _prevLevel + (target - _prevLevel) * (_sampleInStage / (double)len);

        if (++_sampleInStage >= len)
        {
            _prevLevel = target;
            _value = target;
            _stage++;
            _sampleInStage = 0;
            if (_stage > _sustainStage) _value = _levels[_sustainStage];   // settle onto the sustain level
        }
        return _value;
    }

    private static double SumCc(LfoCcDepth[]? mods, ChannelState ch)
    {
        if (mods == null) return 0.0;
        var sum = 0.0;
        for (var i = 0; i < mods.Length; i++)
            sum += ch.GetCC(mods[i].Cc) / 127.0 * mods[i].Amount;
        return sum;
    }

    private static LfoCcDepth[]? Merge(LfoCcDepth[]? a, LfoCcDepth[]? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        var list = new List<LfoCcDepth>(a);
        list.AddRange(b);
        return list.ToArray();
    }
}
