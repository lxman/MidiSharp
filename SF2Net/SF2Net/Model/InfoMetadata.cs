namespace SF2Net;

/// <summary>
/// Decoded contents of the SF2 <c>INFO</c> LIST chunk.
/// </summary>
public sealed class InfoMetadata
{
    /// <summary>Required: SoundFont specification version this file conforms to.</summary>
    public VersionTag SpecVersion { get; set; }

    /// <summary>Required: target sound engine (<c>isng</c>).</summary>
    public string SoundEngine { get; set; } = string.Empty;

    /// <summary>Required: SoundFont bank name (<c>INAM</c>).</summary>
    public string BankName { get; set; } = string.Empty;

    /// <summary>Optional: ROM name (<c>irom</c>).</summary>
    public string? RomName { get; set; }

    /// <summary>Optional: ROM version (<c>iver</c>).</summary>
    public VersionTag? RomVersion { get; set; }

    /// <summary>Optional: creation date (<c>ICRD</c>).</summary>
    public string? CreationDate { get; set; }

    /// <summary>Optional: sound designer / engineer (<c>IENG</c>).</summary>
    public string? Engineer { get; set; }

    /// <summary>Optional: product name (<c>IPRD</c>).</summary>
    public string? Product { get; set; }

    /// <summary>Optional: copyright (<c>ICOP</c>).</summary>
    public string? Copyright { get; set; }

    /// <summary>Optional: comments (<c>ICMT</c>).</summary>
    public string? Comments { get; set; }

    /// <summary>Optional: software used to create the file (<c>ISFT</c>).</summary>
    public string? Software { get; set; }
}
