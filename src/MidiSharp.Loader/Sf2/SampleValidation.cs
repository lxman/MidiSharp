namespace MidiSharp.Loader.Sf2;

/// <summary>
/// Categories of sample-header defects detected by <see cref="SoundFont.ValidateSamples"/>.
/// Most correspond to MUST/SHOULD rules in SF2 spec §6.1 and §7.10.
/// </summary>
public enum SampleValidationCode
{
    /// <summary>End offset is not strictly greater than Start.</summary>
    StartNotBeforeEnd,
    /// <summary>End offset exceeds the frame count of the smpl chunk.</summary>
    SampleExceedsSmpl,
    /// <summary>Sample is shorter than the 48-frame minimum (spec §6.1).</summary>
    SampleTooShort,

    /// <summary>StartLoop precedes Start.</summary>
    LoopStartBeforeSampleStart,
    /// <summary>EndLoop exceeds End.</summary>
    LoopEndAfterSampleEnd,
    /// <summary>StartLoop is not strictly before EndLoop.</summary>
    LoopStartNotBeforeLoopEnd,
    /// <summary>Loop length is below the 32-frame minimum (spec §6.1).</summary>
    LoopTooShort,
    /// <summary>Fewer than 8 frames between Start and StartLoop (spec §6.1).</summary>
    LoopStartMarginTooSmall,
    /// <summary>Fewer than 8 frames between EndLoop and End (spec §6.1).</summary>
    LoopEndMarginTooSmall,

    /// <summary>SampleRate is not in the spec-allowed 400–50000 Hz range.</summary>
    SampleRateOutOfRange,
    /// <summary>OriginalPitch is not 0–127 and not the sentinel 255.</summary>
    OriginalPitchInvalid,

    /// <summary>SampleLink index is out of range for a stereo sample.</summary>
    StereoLinkInvalid,
    /// <summary>SampleLink target is not the opposite L/R partner.</summary>
    StereoLinkTypeMismatch,
    /// <summary>SampleLink target has a different length than this sample.</summary>
    StereoLinkLengthMismatch,
}

/// <summary>
/// One defect found on one sample header during <see cref="SoundFont.ValidateSamples"/>.
/// </summary>
public sealed class SampleValidationIssue
{
    public int SampleIndex { get; }
    public string SampleName { get; }
    public SampleValidationCode Code { get; }
    public string Message { get; }

    public SampleValidationIssue(int index, string name, SampleValidationCode code, string message)
    {
        SampleIndex = index;
        SampleName = name;
        Code = code;
        Message = message;
    }

    public override string ToString() => $"[{SampleIndex}] '{SampleName}': {Code} — {Message}";
}
