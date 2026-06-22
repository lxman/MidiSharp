using System;
using System.IO;

namespace MidiSharp.Hosting.MacEditorHarness;

/// <summary>Locates the built macOS native test fixtures (see tests/fixtures/mac/build-fixtures.sh). Override
/// with the MIDISHARP_MAC_FIXTURES env var; otherwise resolved relative to the harness assembly.</summary>
internal static class MacFixtures
{
    public static string Dir
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("MIDISHARP_MAC_FIXTURES");
            if (!string.IsNullOrEmpty(env)) return env;
            // bin/Debug/net10.0 -> repo root is five levels up, then tests/fixtures/mac/out.
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "tests", "fixtures", "mac", "out"));
        }
    }

    public static bool Has(string fileName) => File.Exists(Path.Combine(Dir, fileName))
                                            || Directory.Exists(Path.Combine(Dir, fileName));
}
