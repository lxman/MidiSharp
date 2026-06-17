using System;
using System.Collections.Generic;

namespace Loader.Sf2;

/// <summary>
/// Validation failure codes (1:1 with the C++ <c>violations</c> enum from sflib).
/// </summary>
public enum SoundFontValidationCode
{
    BadFileName,
    RiffChunkTooLarge,
    RiffChunkTooSmall,
    FileBroken,
    IfilMissing,
    IsngMissing,
    InamMissing,
    IfilBadLength,

    PhdrChunkBad,
    PbagChunkBad,
    PmodChunkBad,
    PgenChunkBad,
    InstChunkBad,
    IbagChunkBad,
    ImodChunkBad,
    IgenChunkBad,
    ShdrChunkBad,

    PresetNdxNonMonotonic,
    PbagCountBad,
    PbagGenNdxNonMonotonic,
    PbagModNdxNonMonotonic,
    PbagGenCountBad,
    PbagModCountBad,

    InstNdxNonMonotonic,
    IbagCountBad,
    IbagGenNdxNonMonotonic,
    IbagModNdxNonMonotonic,
    IbagGenCountBad,
    IbagModCountBad,
}

/// <summary>
/// Thrown when a SoundFont file fails to parse or violates the SF2 specification.
/// </summary>
public class SoundFontException : Exception
{
    public IReadOnlyList<SoundFontValidationCode> Codes { get; }

    public SoundFontException(SoundFontValidationCode code, string? message = null)
        : base(message ?? code.ToString())
    {
        Codes = [code];
    }

    public SoundFontException(IReadOnlyList<SoundFontValidationCode> codes, string? message = null)
        : base(message ?? string.Join(", ", codes))
    {
        Codes = codes;
    }
}
