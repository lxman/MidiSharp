using System.Collections.Generic;

namespace MidiSharp.Synth;

/// <summary>
/// Per-drum-key parameter overrides set via Roland GS drum-part NRPNs (MSB 0x18..0x1E).
/// Null fields mean "no override — use whatever the SF2 zone configured at NoteOn time".
/// </summary>
public sealed class DrumKeyOverride
{
    /// <summary>NRPN 0x18 — Drum coarse pitch in semitones (-64..+63). Null = no override.</summary>
    public sbyte? CoarseTune { get; set; }

    /// <summary>NRPN 0x19 — Drum fine pitch in semitones/100 (-64..+63). Null = no override.</summary>
    public sbyte? FineTune { get; set; }

    /// <summary>NRPN 0x1A — Drum level scalar (0..127). Null = no override (default 127).</summary>
    public byte? Level { get; set; }

    /// <summary>NRPN 0x1C — Drum pan override (0..127, 64=center, 0=random). Null = no override.</summary>
    public byte? Pan { get; set; }

    /// <summary>NRPN 0x1D — Drum reverb send (0..127). Null = no override.</summary>
    public byte? ReverbSend { get; set; }

    /// <summary>NRPN 0x1E — Drum chorus send (0..127). Null = no override.</summary>
    public byte? ChorusSend { get; set; }
}

/// <summary>
/// Channel state for MIDI channel parameters.
/// </summary>
public sealed class ChannelState
{
    /// <summary>
    /// Current bank number used for preset lookup. Set by Synthesizer when CC 0 / CC 32
    /// arrive, or directly forced to 128 for the drum channel. Resolved value combining
    /// MSB and LSB per the GS-style "variation" convention used by GeneralUser-GS and
    /// most modern SF2s (LSB is the variation number; MSB stays 0 for melodic, =128 for drums).
    /// </summary>
    public ushort Bank { get; set; }

    /// <summary>Raw CC 0 Bank Select MSB last received on this channel.</summary>
    public byte BankMsb { get; set; }

    /// <summary>Raw CC 32 Bank Select LSB last received on this channel.</summary>
    public byte BankLsb { get; set; }

    /// <summary>True when this channel should look up presets in bank 128 (drum bank),
    /// either by GM default (channel 10) or by GS "Use For Rhythm Part" SysEx.</summary>
    public bool IsDrumPart { get; set; }

    /// <summary>Drum bank number for this channel when <see cref="IsDrumPart"/> is true.
    /// 128 = GS MAP1 / GM percussion bank (default). 127 = GS MAP2 (some SF2 kits use this).
    /// Set by the value byte of GS "Use For Rhythm Part" (1=MAP1, 2=MAP2).</summary>
    public ushort DrumBank { get; set; } = 128;

    /// <summary>Per-drum-key parameter overrides (GS NRPN 0x18..0x1E). Lazily allocated —
    /// null until the first override arrives. Keyed by MIDI note number.</summary>
    public Dictionary<int, DrumKeyOverride>? DrumOverrides { get; set; }

    /// <summary>
    /// Current program number (0-127).
    /// </summary>
    public byte Program { get; set; }

    /// <summary>
    /// Volume (CC7, 0-127).
    /// </summary>
    public byte Volume { get; set; } = 100;

    /// <summary>
    /// Expression (CC11, 0-127).
    /// </summary>
    public byte Expression { get; set; } = 127;

    /// <summary>
    /// Pan (CC10, 0-127, 64=center).
    /// </summary>
    public byte Pan { get; set; } = 64;

    /// <summary>
    /// Pitch bend (0-16383, 8192=center).
    /// </summary>
    public ushort PitchBend { get; set; } = 8192;

    /// <summary>
    /// Pitch bend range in semitones.
    /// </summary>
    public byte PitchBendRange { get; set; } = 12;

    /// <summary>
    /// Modulation wheel (CC1, 0-127). Drives SF2 default modulator #4
    /// (mod wheel → vibrato LFO pitch depth, amount 50 cents, linear-unipolar).
    /// </summary>
    public byte Modulation { get; set; }

    /// <summary>
    /// Channel pressure / monophonic aftertouch (0-127). Drives SF2 default modulator #3
    /// (channel pressure → vibrato LFO pitch depth, amount 50 cents, linear-unipolar).
    /// </summary>
    public byte ChannelPressure { get; set; }

    /// <summary>
    /// Sustain pedal state (CC64).
    /// </summary>
    public bool Sustain { get; set; }

    /// <summary>
    /// Sostenuto pedal state (CC66). Captures only voices already sounding when the
    /// pedal goes down — later NoteOns are unaffected.
    /// </summary>
    public bool Sostenuto { get; set; }

    /// <summary>
    /// Soft pedal state (CC67). When ≥ 64 attenuates the channel by ~6 dB.
    /// Continuous value retained so future implementations can scale proportionally.
    /// </summary>
    public byte SoftPedal { get; set; }

    /// <summary>
    /// Channel reverb send level (CC 91). 0-127. Per SF2 default modulator 9 this
    /// adds up to amount=200 (= 20% reverb send) to whatever per-voice ReverbEffectsSend
    /// the patch specifies. Default 40 matches RP-015 (GM/GS recommended).
    /// </summary>
    public byte ReverbSendCc { get; set; } = 40;

    /// <summary>
    /// Channel chorus send level (CC 93). 0-127. Per SF2 default modulator 10 this
    /// adds up to amount=200 (= 20% chorus send) to whatever per-voice ChorusEffectsSend
    /// the patch specifies. Default 0 (RP-015 doesn't define a non-zero default).
    /// </summary>
    public byte ChorusSendCc { get; set; }

    /// <summary>
    /// RPN parameter selector — high byte (CC 101). 0x7F = "no RPN selected".
    /// </summary>
    public byte RpnMsb { get; set; } = 0x7F;

    /// <summary>
    /// RPN parameter selector — low byte (CC 100). 0x7F = "no RPN selected".
    /// </summary>
    public byte RpnLsb { get; set; } = 0x7F;

    /// <summary>
    /// Pitch bend range cents fractional component (set by RPN 0,0 with CC 38 Data Entry LSB).
    /// Combined with PitchBendRange to give total range in cents.
    /// </summary>
    public byte PitchBendRangeCents { get; set; }

    /// <summary>
    /// NRPN parameter selector — high byte (CC 99). 0x7F = "no NRPN selected".
    /// </summary>
    public byte NrpnMsb { get; set; } = 0x7F;

    /// <summary>
    /// NRPN parameter selector — low byte (CC 98). 0x7F = "no NRPN selected".
    /// </summary>
    public byte NrpnLsb { get; set; } = 0x7F;

    /// <summary>
    /// True if the most recently sent parameter selector was NRPN (CC 98/99) rather
    /// than RPN (CC 100/101). Data Entry (CC 6/38) updates whichever was selected last.
    /// </summary>
    public bool IsNrpnActive { get; set; }

    /// <summary>
    /// Channel coarse tune in semitones (RPN 0,2). -64..+63. Default 0.
    /// </summary>
    public sbyte CoarseTuneSemitones { get; set; }

    /// <summary>
    /// Channel fine tune in cents (RPN 0,1). 14-bit signed mapped to ±100 cents. Default 0.
    /// </summary>
    public double FineTuneCents { get; set; }

    /// <summary>
    /// Modulation depth range in cents (RPN 0,5). Replaces the SF2 default-modulator-4
    /// 50-cent max for CC1 → vibrato pitch depth. Default 50.
    /// </summary>
    public double ModulationDepthRangeCents { get; set; } = 50.0;

    /// <summary>Tuning Program Select (RPN 0,3). Stored but not used — no MTS table impl.</summary>
    public byte TuningProgram { get; set; }

    /// <summary>Tuning Bank Select (RPN 0,4). Stored but not used.</summary>
    public byte TuningBank { get; set; }

    /// <summary>
    /// CC 8 stereo balance. 0 = full left attenuation, 64 = no attenuation, 127 = full right.
    /// Unlike pan, balance only scales one side of an already-stereo signal — we treat it
    /// as an additional gain multiplier on the L/R outputs (so it works on mono sources too).
    /// </summary>
    public byte Balance { get; set; } = 64;

    /// <summary>
    /// CC 88 High Resolution Velocity Prefix (0-127). When non-zero at NoteOn time, supplies
    /// 7 extra bits of velocity precision (combined with the NoteOn velocity to form a 14-bit
    /// value). Consumed and reset by NoteOn.
    /// </summary>
    public byte HighResVelocityPrefix { get; set; }

    /// <summary>General Purpose Controllers 5-8 (CC 80-83). Stored for completeness;
    /// no SF2-spec'd routing.</summary>
    public byte Gp5 { get; set; }
    /// <inheritdoc cref="Gp5"/>
    public byte Gp6 { get; set; }
    /// <inheritdoc cref="Gp5"/>
    public byte Gp7 { get; set; }
    /// <inheritdoc cref="Gp5"/>
    public byte Gp8 { get; set; }

    /// <summary>CC 92 Tremolo Depth (0=none, 127=max).</summary>
    public byte TremoloDepth { get; set; }
    /// <summary>CC 94 Detune Depth (no processor, stored).</summary>
    public byte DetuneDepth { get; set; }
    /// <summary>CC 95 Phaser Depth (no processor, stored).</summary>
    public byte PhaserDepth { get; set; }

    /// <summary>
    /// CC 71 (Resonance / Timbre): bipolar around 64 (0 mod). Default 64.
    /// Mapped to ±32 cB filter Q offset, applied at NoteOn (live changes don't retro-modify).
    /// </summary>
    public byte FilterResonanceCc { get; set; } = 64;

    /// <summary>
    /// CC 74 (Brightness / Filter Cutoff): bipolar around 64. Default 64.
    /// Mapped to ±6400 cents live filter-cutoff offset, applied per-sample inside Voice.Process.
    /// </summary>
    public byte FilterCutoffCc { get; set; } = 64;

    /// <summary>CC 72 Release Time. Bipolar around 64. Adds ±1200 timecents to release envelope at NoteOn.</summary>
    public byte ReleaseTimeCc { get; set; } = 64;

    /// <summary>CC 73 Attack Time. Bipolar around 64. Adds ±1200 timecents to attack envelope at NoteOn.</summary>
    public byte AttackTimeCc { get; set; } = 64;

    /// <summary>CC 75 Decay Time. Bipolar around 64. Adds ±1200 timecents to decay envelope at NoteOn.</summary>
    public byte DecayTimeCc { get; set; } = 64;

    /// <summary>CC 76 Vibrato Rate. Bipolar around 64. Adds ±1200 cents to vibrato LFO freq at NoteOn.</summary>
    public byte VibratoRateCc { get; set; } = 64;

    /// <summary>CC 77 Vibrato Depth. Bipolar around 64. Adds ±50 cents to channel vibrato depth.</summary>
    public byte VibratoDepthCc { get; set; } = 64;

    /// <summary>CC 78 Vibrato Delay. Bipolar around 64. Adds ±1200 timecents to vibrato LFO delay at NoteOn.</summary>
    public byte VibratoDelayCc { get; set; } = 64;

    /// <summary>
    /// CC 71 contribution to filter Q in centibels.
    /// </summary>
    public double FilterQOffsetCb => (FilterResonanceCc - 64) * 0.5;

    /// <summary>
    /// CC 74 contribution to filter cutoff in cents.
    /// </summary>
    public double FilterCutoffOffsetCents => (FilterCutoffCc - 64) * 100.0;

    /// <summary>
    /// CC 65 portamento on/off. When on, every NoteOn glides from the previous note's
    /// pitch (or PortamentoSourceKey if CC 84 was sent) to the new note's pitch.
    /// </summary>
    public bool PortamentoOn { get; set; }

    /// <summary>
    /// CC 5 portamento time, 0-127. 0 = instant, 127 = very slow.
    /// </summary>
    public byte PortamentoTimeCc { get; set; }

    /// <summary>
    /// CC 84 portamento source key override. -1 means "use LastNoteKey instead".
    /// Set to a MIDI key by CC 84; consumed (and reset to -1) on the next NoteOn.
    /// </summary>
    public sbyte PortamentoSourceKey { get; set; } = -1;

    /// <summary>
    /// Most-recently-played key on this channel (-1 if none yet). Used as portamento
    /// source when CC 65 is on and CC 84 hasn't overridden.
    /// </summary>
    public sbyte LastNoteKey { get; set; } = -1;

    /// <summary>
    /// Gets the volume as a 0-1 value.
    /// </summary>
    public double VolumeNormalized => Volume / 127.0;

    /// <summary>
    /// Gets the expression as a 0-1 value.
    /// </summary>
    public double ExpressionNormalized => Expression / 127.0;

    /// <summary>
    /// Gets the pitch bend in cents. Range is PitchBendRange semitones plus the
    /// PitchBendRangeCents fractional cents (settable via RPN 0,0 + Data Entry).
    /// </summary>
    public double PitchBendCents =>
        (PitchBend - 8192) / 8192.0 * (PitchBendRange * 100 + PitchBendRangeCents);

    /// <summary>
    /// Total channel pitch offset in cents: pitch bend + RPN 0,1 fine tune + RPN 0,2 coarse tune.
    /// This is what gets handed down to each voice.
    /// </summary>
    public double TotalPitchOffsetCents =>
        PitchBendCents + FineTuneCents + CoarseTuneSemitones * 100.0;

    /// <summary>
    /// Gets the pan as a -1 to 1 value.
    /// </summary>
    public double PanNormalized => (Pan - 64) / 64.0;

    /// <summary>
    /// Resets all controllers to defaults.
    /// </summary>
    public void Reset()
    {
        Volume = 100;
        Expression = 127;
        Pan = 64;
        PitchBend = 8192;
        PitchBendRange = 12;
        PitchBendRangeCents = 0;
        RpnMsb = 0x7F;
        RpnLsb = 0x7F;
        Modulation = 0;
        ChannelPressure = 0;
        Sustain = false;
        Sostenuto = false;
        SoftPedal = 0;
        ReverbSendCc = 40;
        ChorusSendCc = 0;
        NrpnMsb = 0x7F;
        NrpnLsb = 0x7F;
        IsNrpnActive = false;
        CoarseTuneSemitones = 0;
        FineTuneCents = 0;
        FilterResonanceCc = 64;
        FilterCutoffCc = 64;
        PortamentoOn = false;
        PortamentoTimeCc = 0;
        PortamentoSourceKey = -1;
        // LastNoteKey deliberately NOT reset by Reset() — controllers reset but the
        // sequencer's last-played key on the channel doesn't disappear mid-piece.
        ModulationDepthRangeCents = 50.0;
        TuningProgram = 0;
        TuningBank = 0;
        Balance = 64;
        HighResVelocityPrefix = 0;
        Gp5 = Gp6 = Gp7 = Gp8 = 0;
        TremoloDepth = 0;
        DetuneDepth = 0;
        PhaserDepth = 0;
        ReleaseTimeCc = 64;
        AttackTimeCc = 64;
        DecayTimeCc = 64;
        VibratoRateCc = 64;
        VibratoDepthCc = 64;
        VibratoDelayCc = 64;
        // Per GS spec, "Reset All Controllers" does NOT clear drum NRPN overrides.
        // Only a full GM/GS Reset (handled by Synthesizer.Reset) clears DrumOverrides.
    }

    /// <summary>
    /// Resets a channel fully including bank/program. Used by Synthesizer.Reset()
    /// for GM/GS Reset SysEx — drops bank-select state entirely.
    /// </summary>
    public void ResetAll()
    {
        Bank = 0;
        BankMsb = 0;
        BankLsb = 0;
        Program = 0;
        Reset();
    }
}