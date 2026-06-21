# Windows Editor-Host Backend (Plan A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Win32 native-window backend to `MidiSharp.Hosting.EditorHost` so plugin editors can open on Windows through the existing `EditorPlatform`/`INativeEditorWindow` seam, verified end-to-end by a managed fake editor that embeds a real child HWND — at structural parity with the X11 backend.

**Architecture:** Mirror the X11 trio (`X11.cs` / `EditorRunLoop.cs` / `X11EditorWindow.cs`) with a Win32 trio (`Win32.cs` / `Win32RunLoop.cs` / `Win32EditorWindow.cs` + `Win32Platform`). The format-agnostic `EditorSession`/`EditorWindow` and the out-of-process worker are unchanged; they light up on Windows once `EditorPlatform.Select()` returns the Win32 backend. The run loop uses `MsgWaitForMultipleObjectsEx(timeout = nearest due timer)` + a `PeekMessage` drain instead of libc `poll()`; `RegisterFd` is a no-op (Windows plugins don't use POSIX fds).

**Tech Stack:** C# (net10.0), `user32.dll`/`gdi32.dll` via P/Invoke (no new package references), xUnit v3.

**Spec:** `docs/superpowers/specs/2026-06-21-windows-editor-host-design.md` (§5.1, §9, §10 item 1). This plan covers the backend only; per-format embedding + native fixtures are Plan B.

## Global Constraints

- **Target framework:** `net10.0` (the `EditorHost` and test projects already target this).
- **Dependency-free:** only `user32.dll` / `gdi32.dll` P/Invoke — **no new package references**, no WinForms/WPF/WinUI.
- **Unicode APIs:** use the `...W` entry points (`CreateWindowExW`, `RegisterClassExW`, `PeekMessageW`, `DefWindowProcW`, `DispatchMessageW`).
- **Build clean:** 0 warnings, 0 errors (`Directory.Build.props` treats the repo as warning-free).
- **One UI thread:** every window/run-loop call happens on the thread that created the window (already enforced by `EditorSession`/`EditorWindow`).
- **Do not modify** `EditorSession.cs`, `EditorWindow.cs`, `IPluginGui.cs`, `IEditorRunLoop` (in `MidiSharp.Hosting`), or the X11 files. The only edit to existing code is one line in `EditorPlatform.Select()`.
- **`nullable` is enabled** in both projects; new files use nullable annotations.

---

## File Structure

- `src/MidiSharp.Hosting.EditorHost/Win32.cs` *(new)* — the `user32`/`gdi32` P/Invoke slice + structs/constants. Pure interop, no logic. Internal.
- `src/MidiSharp.Hosting.EditorHost/Win32RunLoop.cs` *(new)* — `internal sealed class Win32RunLoop : IEditorRunLoop`. Timers + posted work + the message-pump `Pump`.
- `src/MidiSharp.Hosting.EditorHost/Win32EditorWindow.cs` *(new)* — `internal sealed class Win32Platform : IEditorPlatform` and `internal sealed class Win32EditorWindow : INativeEditorWindow` (both in this file, mirroring `X11EditorWindow.cs` which holds `X11Platform` + `X11EditorWindow`).
- `src/MidiSharp.Hosting.EditorHost/IEditorPlatform.cs:63-68` *(modify)* — add the Windows branch to `Select()`.
- `src/MidiSharp.Hosting.EditorHost/MidiSharp.Hosting.EditorHost.csproj` *(modify)* — add `InternalsVisibleTo` for the test project.
- `tests/MidiSharp.Hosting.Tests/FakeEditorGui.cs` *(new)* — `internal sealed class FakeEditorGui : IPluginGui` that creates a `WS_CHILD` `STATIC` window on `SetParent` (the test stand-in for a plugin editor; no native fixture needed).
- `tests/MidiSharp.Hosting.Tests/Win32EditorWindowTests.cs` *(new)* — the backend tests.

---

### Task 1: Win32 run loop + interop scaffolding

Build the Win32 P/Invoke surface the run loop needs and the run loop itself (timers, posted work, message pump). Make `EditorHost` internals visible to the test project so the run loop can be unit-tested directly.

**Files:**
- Create: `src/MidiSharp.Hosting.EditorHost/Win32.cs`
- Create: `src/MidiSharp.Hosting.EditorHost/Win32RunLoop.cs`
- Modify: `src/MidiSharp.Hosting.EditorHost/MidiSharp.Hosting.EditorHost.csproj`
- Test: `tests/MidiSharp.Hosting.Tests/Win32RunLoopTests.cs`

**Interfaces:**
- Consumes: `IEditorRunLoop` (in `MidiSharp.Hosting`): `RegisterFd(int, Action)`, `UnregisterFd(int)`, `RegisterTimer(long periodMs, object token, Action onTick)`, `UnregisterTimer(object token)`, `Post(Action)`.
- Produces: `internal sealed class Win32RunLoop : IEditorRunLoop` with an extra public method `void Pump(int maxWaitMs)` (called later by `Win32EditorWindow.PumpOnce`). `internal static class Win32` exposing `MsgWaitForMultipleObjectsEx`, `PeekMessageW`, `TranslateMessage`, `DispatchMessageW`, the `MSG` struct, and the constants `PM_REMOVE`, `QS_ALLINPUT`, `MWMO_INPUTAVAILABLE`.

- [ ] **Step 1: Add `InternalsVisibleTo` so internals are unit-testable**

Edit `src/MidiSharp.Hosting.EditorHost/MidiSharp.Hosting.EditorHost.csproj` — add this `ItemGroup` after the existing `<ItemGroup>` with the `ProjectReference`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="MidiSharp.Hosting.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Create the interop file `Win32.cs` (message-pump slice)**

Create `src/MidiSharp.Hosting.EditorHost/Win32.cs`:

```csharp
using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// The slice of Win32 (user32/gdi32) needed to host a plugin editor: register a window class, create a
/// top-level window to embed the editor into, show/resize it, pump its message queue (so resize/close work),
/// and tear it down. This is the Windows analogue of <see cref="X11"/>; all Win32 P/Invoke lives here and in
/// <see cref="Win32EditorWindow"/>. All of it is main-thread work, off the audio path.
/// </summary>
internal static partial class Win32
{
    private const string User32 = "user32.dll";

    // ── Message pump ──
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    public const uint PM_REMOVE = 0x0001;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint MWMO_INPUTAVAILABLE = 0x0004;

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG msg);

    [LibraryImport(User32)]
    public static partial IntPtr DispatchMessageW(in MSG msg);

    // nCount=0 + pHandles=NULL is valid: wait for input or timeout. Returns when a message is queued or the
    // timeout elapses, whichever comes first.
    [LibraryImport(User32)]
    public static partial uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr pHandles, uint milliseconds, uint wakeMask, uint flags);
}
```

- [ ] **Step 3: Create the failing run-loop test**

Create `tests/MidiSharp.Hosting.Tests/Win32RunLoopTests.cs`:

```csharp
using System;
using MidiSharp.Hosting.EditorHost;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// The Win32 editor run loop in isolation: managed timers fire on pump, posted work runs on pump, and fd
/// registration is a harmless no-op (Windows plugins don't use POSIX fds). Windows-only.
/// </summary>
public sealed class Win32RunLoopTests
{
    [Fact]
    public void Timer_fires_and_posted_work_runs_on_pump()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 run loop is Windows-only.");

        var loop = new Win32RunLoop();

        var ticks = 0;
        loop.RegisterTimer(5, new object(), () => ticks++);
        for (var i = 0; i < 30 && ticks == 0; i++) loop.Pump(10);
        Assert.True(ticks > 0, "a registered timer should fire while pumping.");

        var posted = false;
        loop.Post(() => posted = true);
        loop.Pump(0);
        Assert.True(posted, "posted work should run on the next pump.");

        // RegisterFd is a no-op on Windows; it must not throw and must not break pumping.
        loop.RegisterFd(0, () => { });
        loop.Pump(0);
        loop.UnregisterFd(0);
    }

    [Fact]
    public void Unregistered_timer_stops_firing()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 run loop is Windows-only.");

        var loop = new Win32RunLoop();
        var token = new object();
        var ticks = 0;
        loop.RegisterTimer(5, token, () => ticks++);
        for (var i = 0; i < 10; i++) loop.Pump(10);
        loop.UnregisterTimer(token);
        var after = ticks;
        for (var i = 0; i < 10; i++) loop.Pump(10);
        Assert.Equal(after, ticks);
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~Win32RunLoopTests"`
Expected: FAIL — compile error, `Win32RunLoop` does not exist.

- [ ] **Step 5: Implement `Win32RunLoop`**

Create `src/MidiSharp.Hosting.EditorHost/Win32RunLoop.cs`:

```csharp
using System;
using System.Collections.Generic;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.EditorHost.Win32;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// The editor UI thread's run loop on Windows. The Windows analogue of <see cref="EditorRunLoop"/>: instead
/// of <c>poll()</c>ing fds it waits on the thread's message queue with <c>MsgWaitForMultipleObjectsEx</c>
/// (timeout = the nearest due timer), then drains the queue with <c>PeekMessage</c>, fires due timers, and
/// runs posted work — all on the UI thread. CLAP <c>clap.timer-support</c> and VST2 <c>effEditIdle</c> map
/// onto the timers; VST3 editors self-drive via the message pump. <c>RegisterFd</c> is a no-op: CLAP
/// <c>clap.posix-fd-support</c> is POSIX-only and Windows plugins do not register fds.
/// </summary>
/// <remarks>
/// Timers/posted work are mutated on the UI thread (from plugin callbacks); a lock still guards them against
/// the rare cross-thread <see cref="Post"/>.
/// </remarks>
internal sealed class Win32RunLoop : IEditorRunLoop
{
    private sealed class Timer { public long Period; public long NextDue; public object Token = null!; public Action OnTick = null!; }

    private readonly object _lock = new();
    private readonly List<Timer> _timers = [];
    private readonly Queue<Action> _posted = [];

    // No-op on Windows: plugins drive their editors via the message pump, not POSIX fds.
    public void RegisterFd(int fd, Action onReady) { }
    public void UnregisterFd(int fd) { }

    public void RegisterTimer(long periodMs, object token, Action onTick)
    {
        if (periodMs < 1) periodMs = 1;
        lock (_lock)
        {
            _timers.RemoveAll(t => Equals(t.Token, token));
            _timers.Add(new Timer { Period = periodMs, NextDue = Environment.TickCount64 + periodMs, Token = token, OnTick = onTick });
        }
    }

    public void UnregisterTimer(object token)
    {
        lock (_lock) _timers.RemoveAll(t => Equals(t.Token, token));
    }

    public void Post(Action action)
    {
        lock (_lock) _posted.Enqueue(action);
    }

    /// <summary>
    /// One iteration: wait on the thread's message queue (timeout = nearest due timer, capped at
    /// <paramref name="maxWaitMs"/>), drain all queued window messages, fire due timers, then run posted work.
    /// </summary>
    public void Pump(int maxWaitMs)
    {
        if (maxWaitMs < 0) maxWaitMs = 0;

        int timeout;
        var now = Environment.TickCount64;
        lock (_lock)
        {
            timeout = maxWaitMs;
            foreach (var t in _timers) { var d = (int)Math.Max(0, t.NextDue - now); if (d < timeout) timeout = d; }
        }

        MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, (uint)timeout, QS_ALLINPUT, MWMO_INPUTAVAILABLE);

        // Drain every queued message; the window's WndProc handles WM_CLOSE etc. during dispatch.
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(in msg);
            DispatchMessageW(in msg);
        }

        // Fire due timers (snapshot so a callback can register/unregister without disturbing iteration).
        now = Environment.TickCount64;
        List<Timer> due = [];
        lock (_lock)
            foreach (var t in _timers)
                if (now >= t.NextDue) { due.Add(t); t.NextDue = now + t.Period; }
        foreach (var t in due) { try { t.OnTick(); } catch { } }

        // Posted work.
        while (true)
        {
            Action? a;
            lock (_lock) a = _posted.Count > 0 ? _posted.Dequeue() : null;
            if (a == null) break;
            try { a(); } catch { }
        }
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~Win32RunLoopTests"`
Expected: PASS (2 tests) on Windows; SKIP on Linux/macOS.

- [ ] **Step 7: Commit**

```bash
git add src/MidiSharp.Hosting.EditorHost/Win32.cs src/MidiSharp.Hosting.EditorHost/Win32RunLoop.cs src/MidiSharp.Hosting.EditorHost/MidiSharp.Hosting.EditorHost.csproj tests/MidiSharp.Hosting.Tests/Win32RunLoopTests.cs
git commit -m "Add Win32 editor run loop (message pump + timers)"
```

---

### Task 2: Win32 host window + platform + Select() wiring

Add the window-creation P/Invoke, the `Win32EditorWindow` host window (embed/resize/close/child-count), the `Win32Platform` factory, and wire it into `EditorPlatform.Select()`. Tested through the public `INativeEditorWindow` surface.

**Files:**
- Modify: `src/MidiSharp.Hosting.EditorHost/Win32.cs` (append window/class/child interop)
- Create: `src/MidiSharp.Hosting.EditorHost/Win32EditorWindow.cs`
- Modify: `src/MidiSharp.Hosting.EditorHost/IEditorPlatform.cs:63-68`
- Test: `tests/MidiSharp.Hosting.Tests/Win32PlatformTests.cs`

**Interfaces:**
- Consumes: `INativeEditorWindow` / `IEditorPlatform` (in `EditorHost`), `Win32RunLoop` (Task 1), the `Win32` interop (Task 1 + this task).
- Produces: `internal sealed class Win32Platform : IEditorPlatform`; `internal sealed class Win32EditorWindow : INativeEditorWindow` with `WindowApi => "win32"`, `Handle` = the HWND as `ulong`, `RunLoop` = a `Win32RunLoop`, and `internal bool Open(string title, int width, int height)`. `EditorPlatform.Select()` returns `Win32Platform` on Windows.

- [ ] **Step 1: Append the window/class/child interop to `Win32.cs`**

Add these members inside the `Win32` class in `src/MidiSharp.Hosting.EditorHost/Win32.cs` (before the closing brace):

```csharp
    // ── Window class + creation ──
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const int SW_SHOW = 5;
    public const int SW_HIDE = 0;
    public const uint WM_CLOSE = 0x0010;
    public const int GW_CHILD = 5;
    public const int GW_HWNDNEXT = 2;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const int BLACK_BRUSH = 4;   // GetStockObject index
    public static readonly IntPtr CW_USEDEFAULT = unchecked((IntPtr)(int)0x80000000);
    // PerMonitorV2 DPI awareness context (Win10 1703+).
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterClassW(string className, IntPtr hInstance);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        IntPtr x, IntPtr y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport(User32)]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32)]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustWindowRectEx(ref RECT rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("gdi32.dll")]
    public static extern IntPtr GetStockObject(int index);
```

Note: this task mixes `[DllImport]` (for the structs/delegate-pointer entry points) with the Task-1 `[LibraryImport]` members; both are fine in one `partial` class. Keep the `partial` keyword on the class as written in Task 1.

- [ ] **Step 2: Create the failing platform test**

Create `tests/MidiSharp.Hosting.Tests/Win32PlatformTests.cs`:

```csharp
using System;
using MidiSharp.Hosting.EditorHost;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// The Win32 windowing backend at the platform/window level (no plugin): EditorPlatform selects it on
/// Windows, a host window is created with a real HWND and an empty child set, resize doesn't throw, and the
/// run loop pumps. Self-skips on a non-interactive desktop.
/// </summary>
[Collection("EditorWindows")]
public sealed class Win32PlatformTests
{
    [Fact]
    public void Creates_a_host_window_with_a_handle_and_pumps()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");

        using var window = EditorPlatform.Current.CreateWindow("Win32 platform test", 320, 240);
        Assert.NotNull(window);
        Assert.Equal("win32", window!.WindowApi);
        Assert.NotEqual(0UL, window.Handle);
        Assert.False(window.ShouldClose);
        Assert.Equal(0u, window.EmbeddedChildCount);   // nothing embedded yet

        window.Map();
        window.Resize(400, 300);     // must not throw with no child
        window.PumpOnce(10);         // exercises the run loop's message pump
        Assert.False(window.ShouldClose);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~Win32PlatformTests"`
Expected: FAIL — compile error, `Win32Platform`/`Win32EditorWindow` do not exist (and `Select()` doesn't return one).

- [ ] **Step 4: Implement `Win32Platform` + `Win32EditorWindow`**

Create `src/MidiSharp.Hosting.EditorHost/Win32EditorWindow.cs`:

```csharp
using System;
using System.Threading;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.EditorHost.Win32;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>The Windows (Win32) windowing backend.</summary>
internal sealed class Win32Platform : IEditorPlatform
{
    private static int _dpiTried;
    private static bool? _available;

    static Win32Platform() { }

    public bool IsAvailable => _available ??= Detect();

    public INativeEditorWindow? CreateWindow(string title, int width, int height)
    {
        EnsureDpiAware();
        var w = new Win32EditorWindow();
        return w.Open(title, width, height) ? w : null;
    }

    // Set per-process DPI awareness once; harmless if the host already set it (returns false → ignored).
    private static void EnsureDpiAware()
    {
        if (Interlocked.Exchange(ref _dpiTried, 1) != 0) return;
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
    }

    // True when a window can actually be created (an interactive desktop). Creating a hidden top-level
    // "STATIC" window needs no class registration and fails on a non-interactive station (a service).
    private static bool Detect()
    {
        try
        {
            var hwnd = CreateWindowExW(0, "STATIC", null, 0, IntPtr.Zero, IntPtr.Zero, 0, 0,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return false;
            DestroyWindow(hwnd);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>
/// A top-level Win32 window that hosts an embedded plugin editor: it owns the window, a per-instance window
/// class with a black background, and a message-pump <see cref="Win32RunLoop"/> that services the window's
/// messages alongside the plugin's timers. The Windows analogue of <see cref="X11EditorWindow"/>; all Win32
/// detail is confined to this class (and <see cref="Win32"/>), so the rest of the editor host is
/// windowing-agnostic. Created and pumped on a single UI thread.
/// </summary>
internal sealed class Win32EditorWindow : INativeEditorWindow
{
    private static int _seq;

    private readonly Win32RunLoop _runLoop = new();
    private readonly WndProc _wndProc;          // kept alive for the window's lifetime (GC root)
    private readonly string _className;
    private IntPtr _hwnd;
    private IntPtr _hInstance;
    private bool _shouldClose;

    public Win32EditorWindow()
    {
        _wndProc = WindowProc;
        _className = $"MidiSharpEdHost_{Environment.ProcessId}_{Interlocked.Increment(ref _seq)}";
    }

    public string WindowApi => "win32";
    public ulong Handle => (ulong)_hwnd;
    public IEditorRunLoop RunLoop => _runLoop;
    public bool ShouldClose => _shouldClose;

    public uint EmbeddedChildCount
    {
        get
        {
            if (_hwnd == IntPtr.Zero) return 0;
            uint n = 0;
            for (var c = GetWindow(_hwnd, GW_CHILD); c != IntPtr.Zero; c = GetWindow(c, GW_HWNDNEXT)) n++;
            return n;
        }
    }

    internal bool Open(string title, int width, int height)
    {
        _hInstance = GetModuleHandleW(null);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            // Background BLACK (not white): the embedded plugin owns the pixels, but a white parent shows
            // through compositing seams as bright lines behind a dark editor (same fix as the X11 backend).
            hbrBackground = GetStockObject(BLACK_BRUSH),
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref wc) == 0) return false;

        var (winW, winH) = WindowSizeForClient(width, height);
        _hwnd = CreateWindowExW(0, _className, title, WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, winW, winH, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) { UnregisterClassW(_className, _hInstance); return false; }
        return true;
    }

    public void Resize(int width, int height)
    {
        if (_hwnd == IntPtr.Zero || width <= 0 || height <= 0) return;
        var (winW, winH) = WindowSizeForClient(width, height);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, winW, winH, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        var child = GetWindow(_hwnd, GW_CHILD);
        if (child != IntPtr.Zero)
            SetWindowPos(child, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void Map() => ShowWindow(_hwnd, SW_SHOW);

    public void CompleteEmbed() { }   // no XEMBED-style handshake on Win32

    public void PumpOnce(int maxWaitMs) => _runLoop.Pump(maxWaitMs);

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_CLOSE) { _shouldClose = true; return IntPtr.Zero; }   // host controls teardown
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // Outer window size that yields a width×height client area for an overlapped window.
    private static (int w, int h) WindowSizeForClient(int width, int height)
    {
        var r = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        AdjustWindowRectEx(ref r, WS_OVERLAPPEDWINDOW, false, 0);
        return (r.Right - r.Left, r.Bottom - r.Top);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_hInstance != IntPtr.Zero) { UnregisterClassW(_className, _hInstance); _hInstance = IntPtr.Zero; }
    }
}
```

- [ ] **Step 5: Wire the backend into `EditorPlatform.Select()`**

Edit `src/MidiSharp.Hosting.EditorHost/IEditorPlatform.cs`. Replace the body of `Select()` (lines 63-68):

```csharp
    private static IEditorPlatform Select()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) return new X11Platform();
        // Windows (Win32) and macOS (Cocoa) backends slot in here.
        return new UnsupportedPlatform();
    }
```

with:

```csharp
    private static IEditorPlatform Select()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) return new X11Platform();
        if (OperatingSystem.IsWindows()) return new Win32Platform();
        // macOS (Cocoa) backend slots in here.
        return new UnsupportedPlatform();
    }
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~Win32PlatformTests"`
Expected: PASS on Windows; SKIP on Linux/macOS.

- [ ] **Step 7: Commit**

```bash
git add src/MidiSharp.Hosting.EditorHost/Win32.cs src/MidiSharp.Hosting.EditorHost/Win32EditorWindow.cs src/MidiSharp.Hosting.EditorHost/IEditorPlatform.cs tests/MidiSharp.Hosting.Tests/Win32PlatformTests.cs
git commit -m "Add Win32 host window + platform, wire into EditorPlatform.Select()"
```

---

### Task 3: End-to-end embed via a managed fake editor (acceptance gate)

Prove the full `EditorSession` embed sequence works on Windows with a managed `IPluginGui` that creates a real child HWND — no native plugin or toolchain. This is the backend's always-on regression guard (spec §10 item 1).

**Files:**
- Create: `tests/MidiSharp.Hosting.Tests/FakeEditorGui.cs`
- Create: `tests/MidiSharp.Hosting.Tests/Win32EditorWindowTests.cs`

**Interfaces:**
- Consumes: `IPluginGui` (in `MidiSharp.Hosting`), `EditorWindow.Open(IPluginGui?, string)` (in `EditorHost`), `INativeEditorWindow.EmbeddedChildCount`/`WindowHandle`/`IsOpen`/`Close`.
- Produces: `internal sealed class FakeEditorGui : IPluginGui` (a test stand-in editor).

- [ ] **Step 1: Create the managed fake editor**

Create `tests/MidiSharp.Hosting.Tests/FakeEditorGui.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// A managed <see cref="IPluginGui"/> standing in for a native plugin editor on Windows: on
/// <see cref="SetParent"/> it creates a real WS_CHILD "STATIC" window under the host HWND, exactly as a
/// native editor parents its window. Lets the Win32 editor backend (window, run loop, embed sequence) be
/// verified end-to-end with no native plugin or toolchain — the GUI analogue of <c>FakeGainPlugin</c>.
/// </summary>
internal sealed class FakeEditorGui : IPluginGui
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;

    private readonly int _w;
    private readonly int _h;
    private IntPtr _child;

    public FakeEditorGui(int width = 320, int height = 240) { _w = width; _h = height; }

    public bool HasEditor => true;
    public bool IsApiSupported(string windowApi, bool floating) => windowApi == "win32";
    public bool Create(string windowApi, bool floating) => windowApi == "win32";
    public bool SetScale(double scale) => true;

    public bool TryGetSize(out int width, out int height) { width = _w; height = _h; return true; }

    public bool SetParent(string windowApi, ulong windowHandle)
    {
        if (windowApi != "win32") return false;
        // A built-in "STATIC" control class needs no registration; this is the embedded child the host counts.
        _child = CreateWindowExW(0, "STATIC", null, WS_CHILD | WS_VISIBLE, 0, 0, _w, _h,
            (IntPtr)windowHandle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return _child != IntPtr.Zero;
    }

    public bool Show() => true;
    public bool Hide() => true;

    public void Destroy()
    {
        if (_child != IntPtr.Zero) { DestroyWindow(_child); _child = IntPtr.Zero; }
    }
}
```

- [ ] **Step 2: Create the failing end-to-end embed test**

Create `tests/MidiSharp.Hosting.Tests/Win32EditorWindowTests.cs`:

```csharp
using System;
using System.Threading;
using MidiSharp.Hosting.EditorHost;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// Live Win32 embed through the full EditorSession sequence: open a real top-level window and embed the
/// managed fake editor's child HWND into it, then confirm the host actually parented a child window. The
/// Windows analogue of <see cref="ClapEditorWindowTests"/>; self-skips off Windows or on a non-interactive
/// desktop. No native fixture needed.
/// </summary>
[Collection("EditorWindows")]
public sealed class Win32EditorWindowTests
{
    [Fact]
    public void Embeds_the_fake_editor_in_a_native_window()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Win32 backend is Windows-only.");
        Assert.SkipWhen(!EditorPlatform.Current.IsAvailable, "no interactive desktop.");

        var gui = new FakeEditorGui(320, 240);
        using var window = EditorWindow.Open(gui, "MidiSharp Win32 editor test");
        Assert.NotNull(window);
        Assert.True(window!.IsOpen, $"editor window should be open (error: {window.Error}).");
        Assert.NotEqual(0UL, window.WindowHandle);

        // The fake's SetParent created a child of our host window — give it a moment, then verify.
        uint children = 0;
        for (var i = 0; i < 20 && children == 0; i++) { children = window.EmbeddedChildCount; if (children == 0) Thread.Sleep(50); }
        Assert.True(children >= 1, "the fake editor should have embedded a child window into the host window.");

        window.Close();
        Assert.False(window.IsOpen);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails, then passes**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~Win32EditorWindowTests"`
Expected first run: FAIL — `FakeEditorGui` not found (compile) — then after Step 1 is in place, PASS on Windows / SKIP elsewhere. (If both files are added together, expect PASS on Windows directly.)

- [ ] **Step 4: Run the whole Hosting suite to confirm no regressions**

Run: `dotnet test tests/MidiSharp.Hosting.Tests`
Expected: all pass; Linux-only/X11 and not-yet-built-fixture tests SKIP. No failures.

- [ ] **Step 5: Build the full solution clean**

Run: `dotnet build MidiSharp.slnx -c Debug`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add tests/MidiSharp.Hosting.Tests/FakeEditorGui.cs tests/MidiSharp.Hosting.Tests/Win32EditorWindowTests.cs
git commit -m "Verify Win32 editor backend embeds a child window end-to-end"
```

---

## Self-Review

**Spec coverage (§5.1, §9, §10 item 1):**
- `Win32.cs` P/Invoke slice → Task 1 (message pump) + Task 2 (window/class/child). ✓
- `Win32EditorWindow : INativeEditorWindow` (WindowApi/Handle/Resize/Map/CompleteEmbed/EmbeddedChildCount/ShouldClose/PumpOnce/Dispose) → Task 2. ✓
- `Win32RunLoop : IEditorRunLoop` (timers + posted + message pump; RegisterFd no-op) → Task 1. ✓
- `Win32Platform` + `Select()` wiring → Task 2. ✓
- DPI awareness, black background, rooted WndProc, unique per-process class name → Task 2 (§9). ✓
- Managed `FakeEditorGui` embed test (acceptance gate item 1) → Task 3. ✓
- Out of scope here (Plan B): per-format adapter changes, native fixtures, per-format live tests.

**Placeholder scan:** No TBD/TODO; every code step shows complete code; every run step shows the command and expected result. ✓

**Type consistency:** `Win32RunLoop.Pump(int)` defined in Task 1, called by `Win32EditorWindow.PumpOnce` in Task 2. `Win32EditorWindow.Open(string,int,int)` defined and called by `Win32Platform.CreateWindow` in Task 2. `INativeEditorWindow` members match `IEditorPlatform.cs`. `IPluginGui` members in `FakeEditorGui` (Task 3) match the non-default members of `IPluginGui.cs` (BindRunLoop/Idle use interface defaults). `Win32` is `partial` in both Task 1 and Task 2 additions. ✓

**Note for the implementer:** `[LibraryImport]` (Task 1) and `[DllImport]` (Task 2) coexist in the same `partial class Win32`; keep the `partial` modifier. If the analyzer flags mixing, either is acceptable — do not introduce a new package.
