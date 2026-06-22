using System;
using System.IO;

namespace MidiSharp.Hosting.Tests;

/// <summary>Locates the built Windows native test fixtures (see tests/fixtures/win/build-fixtures.ps1).
/// Override with the MIDISHARP_WIN_FIXTURES env var; otherwise resolved relative to the test assembly.</summary>
internal static class WinFixtures
{
    public static string Dir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("MIDISHARP_WIN_FIXTURES");
            if (!string.IsNullOrEmpty(env)) return env;
            // bin/Debug/net10.0 -> repo root is five levels up, then tests/fixtures/win/out.
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "tests", "fixtures", "win", "out"));
        }
    }

    public static bool Available => Directory.Exists(Dir);
}
