using System;
using System.Collections.Generic;

namespace MidiSharp.SoundBank;

/// <summary>
/// The format-neutral intermediate representation produced by every loader and
/// consumed by the synth. Immutable after construction; safe to share across
/// threads without locking.
/// </summary>
/// <remarks>
/// Owns its <see cref="Samples"/> source — disposing the bank disposes the
/// source, releasing mmap handles and decode caches. Do not dispose while
/// voices from the bank are still sounding; call <c>AllSoundOff</c> first.
/// </remarks>
public sealed class SoundBank : IDisposable
{
    private Dictionary<(int Bank, int Program), Patch>? _patchIndex;
    private bool _disposed;

    /// <summary>Bank name (required).</summary>
    public string Name { get; init; } = string.Empty;

    public string? Author { get; init; }

    public string? Copyright { get; init; }

    public string? Comment { get; init; }

    public SoundBankFormat SourceFormat { get; init; }

    public IReadOnlyList<Patch> Patches { get; init; } = [];

    /// <summary>
    /// The bank's sample source. Disposed when this bank is disposed.
    /// </summary>
    public ISampleSource Samples { get; init; } = EmptySampleSource.Instance;

    /// <summary>
    /// Initial MIDI controller values the instrument expects (SFZ set_ccN/set_hdccN), CC number → 0..127.
    /// The synth seeds channel state with these when the bank is loaded. Empty for SF2/SF3/DLS and SFZ
    /// without set_cc opcodes.
    /// </summary>
    public IReadOnlyDictionary<int, int> InitialControllers { get; init; }
        = new Dictionary<int, int>();

    /// <summary>
    /// The MIDI controller the synth should treat as the sustain pedal for this bank (SFZ sustain_cc).
    /// Default CC64; half-pedal SFZ fonts reassign it (e.g. to CC90), in which case literal CC64 no
    /// longer holds notes. 64 for SF2/SF3/DLS.
    /// </summary>
    public int SustainCc { get; init; } = 64;

    /// <summary>
    /// Look up a patch by (bank, program). Returns null if no match exists.
    /// O(1) — backed by a dictionary built lazily on first call.
    /// </summary>
    public Patch? FindPatch(int bank, int program)
    {
        if (_patchIndex == null)
        {
            var index = new Dictionary<(int, int), Patch>(Patches.Count);
            foreach (Patch? patch in Patches)
            {
                index[(patch.Bank, patch.Program)] = patch;
            }
            _patchIndex = index;
        }

        return _patchIndex.TryGetValue((bank, program), out Patch? found) ? found : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Samples.Dispose();
    }
}
