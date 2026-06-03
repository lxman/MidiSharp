using System;
using System.Collections.Generic;
using MidiSharp.Model;
using IRBank = MidiSharp.SoundBank.SoundBank;

namespace MidiSharp.PatchMap;

/// <summary>
/// The stateful model a UI binds to: a base font, a palette of preloaded source fonts, the
/// patches a song uses, and the user's per-patch overrides. <see cref="BuildComposite"/> yields
/// a composite <see cref="IRBank"/> that the synth consumes unchanged.
/// </summary>
/// <remarks>
/// Owns the lifetime of the base and source fonts — disposing the session disposes them all.
/// Composites returned by <see cref="BuildComposite"/> are lightweight borrowed views whose own
/// disposal does not touch the fonts, so a fresh composite can be built for each playback.
/// </remarks>
public sealed class PatchMapSession : IDisposable
{
    private readonly List<IRBank> _sources = new();
    private readonly Dictionary<(int Bank, int Program), PatchRef> _overrides = new();
    private bool _disposed;

    public PatchMapSession(IRBank baseBank)
        => Base = baseBank ?? throw new ArgumentNullException(nameof(baseBank));

    /// <summary>The base font supplying defaults for every patch the song requests.</summary>
    public IRBank Base { get; }

    /// <summary>Preloaded source fonts the user can pick override patches from.</summary>
    public IReadOnlyList<IRBank> Sources => _sources;

    /// <summary>The current overrides: logical (bank, program) → a patch in a source font.</summary>
    public IReadOnlyDictionary<(int Bank, int Program), PatchRef> Overrides => _overrides;

    /// <summary>Preload a source font to pick override patches from. Returns its palette index.</summary>
    public int AddSource(IRBank font)
    {
        if (font == null) throw new ArgumentNullException(nameof(font));
        _sources.Add(font);
        return _sources.Count - 1;
    }

    /// <summary>List the patches a song uses, named against the base font ("what it normally plays").</summary>
    public IReadOnlyList<UsedPatch> AnalyzeUsage(MidiFile song)
        => PatchUsageAnalyzer.Analyze(song, Base);

    /// <summary>Route a logical (bank, program) to a patch from a source font.</summary>
    public void SetOverride(int bank, int program, PatchRef target)
        => _overrides[(bank, program)] = target;

    /// <summary>Remove any override at the given logical address (revert to the base font).</summary>
    public void ClearOverride(int bank, int program)
        => _overrides.Remove((bank, program));

    /// <summary>Drop all overrides (revert entirely to the base font).</summary>
    public void ClearAllOverrides() => _overrides.Clear();

    /// <summary>
    /// Build the composite bank to hand to the synth. Borrows from the base and source fonts;
    /// safe to call repeatedly (e.g. once per playback). Does not mutate session state.
    /// </summary>
    public IRBank BuildComposite() => SoundBankComposer.BuildComposite(Base, _overrides);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Base.Dispose();
        foreach (var source in _sources) source.Dispose();
    }
}
