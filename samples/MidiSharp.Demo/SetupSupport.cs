using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MidiSharp.Loader;
using MidiSharp.PatchMap;
using MidiSharp.SoundBank;
using IRBank = MidiSharp.SoundBank.SoundBank;

// CLI support for playing a saved "setup" (the web player's per-MIDI instrument-substitution
// configuration). The canonical schema is MidiSharp.Server's SetupDto/SetupStore; we deliberately
// re-declare a read-only mirror here instead of taking a dependency on the web app project. Only the
// fields the CLI player acts on are modelled — System.Text.Json ignores any extra fields the server
// adds later (mixer/EQ/automation), so this stays forward-compatible without code changes.

internal sealed record SetupPatchOverride(int LogicalBank, int LogicalProgram, string SourcePath, int SourceBank, int SourceProgram, double GainDb = 0);

internal sealed record SetupTrackOverride(int TrackIndex, string? TrackName, string SourcePath, int SourceBank, int SourceProgram, double GainDb = 0);

internal sealed record SetupPartOverride(int TrackIndex, int Channel, string? PartName, string SourcePath, int SourceBank, int SourceProgram, double GainDb = 0);

internal sealed record SetupFile(
    string Name,
    string MidiPath,
    string? MidiName,
    string SoundfontPath,
    string? SoundfontName,
    SetupPatchOverride[]? Overrides,
    SetupTrackOverride[]? TrackOverrides,
    SetupPartOverride[]? PartOverrides = null,
    string? SavedAt = null,
    int Version = 1);

internal static class SetupSupport
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Where the web player saves setups (override-able there via --setups-root; the CLI uses the default).</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "midisharp", "setups");

    /// <summary>
    /// Resolves a <c>--setup</c> argument that may be a path to a .json file, a setup id (the file stem),
    /// or a setup's <c>name</c>. Returns null with a populated <paramref name="error"/> when nothing matches.
    /// </summary>
    public static SetupFile? Resolve(string spec, out string? error)
    {
        if (File.Exists(spec)) return Read(spec, out error);

        var root = DefaultRoot;
        if (Directory.Exists(root))
        {
            var byId = Path.Combine(root, spec.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? spec : spec + ".json");
            if (File.Exists(byId)) return Read(byId, out error);

            foreach (var file in Directory.EnumerateFiles(root, "*.json"))
            {
                var s = Read(file, out _);
                if (s != null && string.Equals(s.Name, spec, StringComparison.OrdinalIgnoreCase))
                {
                    error = null;
                    return s;
                }
            }
        }

        error = $"Setup not found: '{spec}' (looked for a file path, id, or name under {root})";
        return null;
    }

    private static SetupFile? Read(string path, out string? error)
    {
        try
        {
            error = null;
            return JsonSerializer.Deserialize<SetupFile>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            error = $"Could not read setup '{path}': {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Builds a <see cref="PatchMapSession"/> over <paramref name="baseBank"/> applying the setup's
    /// patch- and track-overrides, loading each distinct source font once. The caller owns the
    /// returned session and must dispose it (that releases the base + every source font).
    /// Mirrors MidiSharp.Server's PlayerService apply logic.
    /// </summary>
    public static PatchMapSession BuildSession(SetupFile setup, IRBank baseBank, SoundBankLoadOptions loadOptions)
    {
        var session = new PatchMapSession(baseBank);
        var byPath = new Dictionary<string, IRBank>(StringComparer.OrdinalIgnoreCase);

        IRBank Source(string path)
        {
            if (byPath.TryGetValue(path, out var cached)) return cached;
            var src = SoundBankLoader.Load(path, loadOptions);
            session.AddSource(src);
            byPath[path] = src;
            return src;
        }

        foreach (var o in setup.Overrides ?? [])
            session.SetOverride(o.LogicalBank, o.LogicalProgram,
                new PatchRef(Source(o.SourcePath), o.SourceBank, o.SourceProgram));

        foreach (var o in setup.TrackOverrides ?? [])
            session.SetTrackOverride(o.TrackIndex,
                new PatchRef(Source(o.SourcePath), o.SourceBank, o.SourceProgram));

        foreach (var o in setup.PartOverrides ?? [])
            session.SetPartOverride(o.TrackIndex, o.Channel,
                new PatchRef(Source(o.SourcePath), o.SourceBank, o.SourceProgram));

        return session;
    }
}
