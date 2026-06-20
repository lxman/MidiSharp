using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Test classes that open real native editor windows share this collection so they don't run in parallel —
/// two editors mapping at once race on the X server / window manager and make the embed checks flaky.
/// </summary>
[CollectionDefinition("EditorWindows")]
public sealed class EditorWindowsCollection;
