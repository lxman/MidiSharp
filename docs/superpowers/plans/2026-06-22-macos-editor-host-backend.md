# macOS Editor-Host Backend (Plan A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Cocoa native-window backend to `MidiSharp.Hosting.EditorHost` so plugin editors can open on macOS through the existing `EditorPlatform`/`INativeEditorWindow` seam, verified end-to-end by a managed fake editor that embeds a real child `NSView` — at structural parity with the X11 and Win32 backends.

**Architecture:** Mirror the X11/Win32 trio (`X11.cs`/`EditorRunLoop.cs`/`X11EditorWindow.cs`, `Win32.cs`/`Win32RunLoop.cs`/`Win32EditorWindow.cs`) with a Cocoa trio (`Cocoa.cs`/`CocoaRunLoop.cs`/`CocoaEditorWindow.cs` + `CocoaPlatform`). The format-agnostic `EditorSession`/`EditorWindow` and the out-of-process worker are unchanged; they light up on macOS once `EditorPlatform.Select()` returns the Cocoa backend. The run loop pumps the `NSApp` event queue bounded by the nearest due timer (off the main thread it falls back to a `poll()` sleep, so the run loop is unit-testable); `RegisterFd` is a no-op (CLAP `posix-fd-support` deferred on macOS).

**The macOS divergence (already settled in the spec):** AppKit is main-thread-only and xUnit runs tests on pool threads, so the window/embed acceptance gate is a **standalone main-thread console harness**, not an xUnit `[Fact]`. The pure-managed run loop *is* xUnit-tested. The Cocoa backend also **declines to create a window off the main thread** (`pthread_main_np()`), so the in-process/sandbox-off path fails safe on macOS instead of risking a crash; the sandbox worker (which pumps on its process main thread) passes the guard.

**Tech Stack:** C# (net10.0), Objective-C runtime + AppKit/Foundation/CoreGraphics via P/Invoke (`/usr/lib/libobjc.A.dylib`, system frameworks — no new package references), xUnit v3. **arm64 only.**

**Spec:** `docs/superpowers/specs/2026-06-22-macos-editor-host-design.md` (§5.1, §5.3, §7, §9, §10 items 1–2). This plan covers the backend only; per-format embedding + native fixtures are Plan B. The core interop (`objc_msgSend` with `CGRect` by value and by return, `NSView` `addSubview:` embedding, `NSApp` event pump) was **spike-verified on this arm64 Mac on 2026-06-22**.

## Global Constraints

- **Build/test with `dotnet`.** The .NET 10 SDK is on `PATH` (`dotnet --version` → `10.0.301`); plain `dotnet` builds net10.0 (verified: full solution 0/0, existing suite green on this Mac). `timeout`/`gtimeout` is absent on this Mac — don't wrap commands in it.
- **Target framework:** `net10.0` (the `EditorHost` and test projects already target this).
- **Dependency-free:** only `libobjc` + system-framework P/Invoke (`AppKit`, `CoreGraphics`, `libSystem`/`libc`) — **no new package references**, no Xamarin.Mac/MonoMac/MAUI.
- **arm64 only.** `objc_msgSend` is uniform on Apple Silicon (an `NSRect` is an HFA returned in `v0–v3`; no `objc_msgSend_stret`). The backend reports unavailable on x86_64 rather than mis-marshalling.
- **Main thread = the UI thread.** Every AppKit call (create/parent/show/idle/destroy) and every `NSApp` pump runs on the process main thread. The backend guards against off-main-thread window creation; the worker satisfies this for free.
- **Build clean:** 0 warnings, 0 errors. (Discipline target — not enforced via `TreatWarningsAsErrors`, so keep new code clean.)
- **Do not modify** `EditorSession.cs`, `EditorWindow.cs`, `IPluginGui.cs`, `IEditorRunLoop` (in `MidiSharp.Hosting`), or the X11/Win32 files. The only edit to existing code is one line in `EditorPlatform.Select()`.
- **`nullable` is enabled** in both projects; new files use nullable annotations.
- `MidiSharp.Hosting.EditorHost.csproj` **already** has `<InternalsVisibleTo Include="MidiSharp.Hosting.Tests" />` (added for the Win32 work) — no csproj change is needed for the run-loop unit test, unlike Windows Plan A.

---

## File Structure

- `src/MidiSharp.Hosting.EditorHost/Cocoa.cs` *(new)* — the Objective-C runtime + AppKit/Foundation/CoreGraphics P/Invoke slice + structs/selectors/helpers. Pure interop, no window logic. Internal.
- `src/MidiSharp.Hosting.EditorHost/CocoaRunLoop.cs` *(new)* — `internal sealed class CocoaRunLoop : IEditorRunLoop`. Timers + posted work + the `NSApp`-pump `Pump` (with an off-main-thread `poll()` fallback).
- `src/MidiSharp.Hosting.EditorHost/CocoaEditorWindow.cs` *(new)* — `internal sealed class CocoaPlatform : IEditorPlatform` and `internal sealed class CocoaEditorWindow : INativeEditorWindow` (both in this file, mirroring `X11EditorWindow.cs`/`Win32EditorWindow.cs`).
- `src/MidiSharp.Hosting.EditorHost/IEditorPlatform.cs:62-68` *(modify)* — add the macOS branch to `Select()`.
- `tests/MidiSharp.Hosting.Tests/CocoaRunLoopTests.cs` *(new)* — run-loop unit tests (xUnit, always-on, AppKit-free).
- `tests/MidiSharp.Hosting.Tests/CocoaPlatformTests.cs` *(new)* — backend selection + off-main-thread guard (xUnit).
- `tests/MidiSharp.Hosting.MacEditorHarness/MacEditorHarness.csproj` *(new)* — the main-thread embed gate (console).
- `tests/MidiSharp.Hosting.MacEditorHarness/FakeCocoaEditorGui.cs` *(new)* — `IPluginGui` that creates a child `NSView` on `SetParent`.
- `tests/MidiSharp.Hosting.MacEditorHarness/Program.cs` *(new)* — opens an `EditorSession` on thread 0 and asserts the embed.
- `MidiSharp.slnx` *(modify)* — add the harness project under `/Tests/`.

---

### Task 1: Cocoa interop core + run loop

Build the Objective-C/AppKit P/Invoke surface and the run loop (timers, posted work, `NSApp` event pump). The run loop is unit-tested directly: off the main thread (where xUnit runs) it waits via `poll()` instead of `NSApp`, so the timer/posted logic is provable without AppKit.

**Files:**
- Create: `src/MidiSharp.Hosting.EditorHost/Cocoa.cs`
- Create: `src/MidiSharp.Hosting.EditorHost/CocoaRunLoop.cs`
- Test: `tests/MidiSharp.Hosting.Tests/CocoaRunLoopTests.cs`

**Interfaces:**
- Consumes: `IEditorRunLoop` (in `MidiSharp.Hosting`): `RegisterFd(int, Action)`, `UnregisterFd(int)`, `RegisterTimer(long periodMs, object token, Action onTick)`, `UnregisterTimer(object token)`, `Post(Action)`.
- Produces: `internal sealed class CocoaRunLoop : IEditorRunLoop` with an extra public `void Pump(int maxWaitMs)` (called later by `CocoaEditorWindow.PumpOnce`). `internal static class Cocoa` exposing `Send`/`SendUInt`/`SendStr`/`SendDouble`/`SendEvent`/`SendNInt` messaging, `Cls`/`Sel`/`NSString`, `App`/`EnsureApp`, `IsMainThread`, `Sleep`, and `PumpEvents`.

- [ ] **Step 1: Create the interop core `Cocoa.cs`**

Create `src/MidiSharp.Hosting.EditorHost/Cocoa.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
// ReSharper disable InconsistentNaming

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// The slice of the Objective-C runtime + AppKit needed to host a plugin editor on macOS: create an NSWindow
/// with a content NSView, embed the plugin's NSView under it, show/resize it, pump the NSApp event queue, and
/// tear it down. The macOS analogue of <see cref="X11"/>/<see cref="Win32"/>; all libobjc/AppKit P/Invoke lives
/// here and in <see cref="CocoaEditorWindow"/>. arm64 only — objc_msgSend is uniform (an NSRect is an HFA
/// returned in v0–v3, so there is no objc_msgSend_stret). All of it is main-thread work, off the audio path.
/// </summary>
internal static class Cocoa
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    static Cocoa()
    {
        // Bring AppKit (and transitively Foundation) into the process so NSWindow/NSView/... resolve by name.
        if (OperatingSystem.IsMacOS())
            NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    }

    // ── Objective-C messaging: one C# entry per native signature shape, all binding to objc_msgSend ──
    [DllImport(Objc)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Objc)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr recv, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr recv, IntPtr sel, IntPtr a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr recv, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nuint SendUInt(IntPtr recv, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern void SendNInt(IntPtr recv, IntPtr sel, nint a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr SendDouble(IntPtr recv, IntPtr sel, double a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr SendStr(IntPtr recv, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string str);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr SendEvent(IntPtr recv, IntPtr sel, nuint mask, IntPtr until, IntPtr mode, [MarshalAs(UnmanagedType.U1)] bool dequeue);

    // Class handles are immortal; selectors too. Both caches are read on the UI thread but a lock keeps them
    // safe against the rare cross-thread first touch (e.g. an availability probe).
    private static readonly object _lock = new();
    private static readonly Dictionary<string, IntPtr> _classes = new();
    private static readonly Dictionary<string, IntPtr> _sels = new();

    public static IntPtr Cls(string name)
    {
        lock (_lock) { if (!_classes.TryGetValue(name, out IntPtr c)) { c = objc_getClass(name); _classes[name] = c; } return c; }
    }

    public static IntPtr Sel(string name)
    {
        lock (_lock) { if (!_sels.TryGetValue(name, out IntPtr s)) { s = sel_registerName(name); _sels[name] = s; } return s; }
    }

    /// <summary>An autoreleased NSString from a managed string (valid until the enclosing pool drains).</summary>
    public static IntPtr NSString(string s) => SendStr(Cls("NSString"), Sel("stringWithUTF8String:"), s);

    // ── NSApplication bootstrap (idempotent; main-thread) ──
    private static IntPtr _app;
    public static IntPtr App => _app != IntPtr.Zero ? _app : _app = Send(Cls("NSApplication"), Sel("sharedApplication"));

    /// <summary>Ensure NSApp exists with a background-helper activation policy. Main-thread only.</summary>
    public static void EnsureApp() => SendNInt(App, Sel("setActivationPolicy:"), 1);   // NSApplicationActivationPolicyAccessory

    // ── Run-loop primitives ──
    [DllImport("libc")] private static extern int pthread_main_np();
    [DllImport("libc")] private static extern int poll(IntPtr fds, uint nfds, int timeout);

    /// <summary>True on the process main thread — the only thread AppKit may be driven from.</summary>
    public static bool IsMainThread => pthread_main_np() != 0;

    /// <summary>A timed sleep with no AppKit dependency (poll with no fds). The off-main-thread wait.</summary>
    public static void Sleep(int ms) { if (ms > 0) poll(IntPtr.Zero, 0, ms); }

    /// <summary>Drain the NSApp event queue, blocking up to <paramref name="timeoutMs"/> for the first event
    /// then taking the rest without blocking. Main-thread only; the steady-state editor pump.</summary>
    public static void PumpEvents(int timeoutMs)
    {
        IntPtr app = App;
        IntPtr mode = NSString("kCFRunLoopDefaultMode");   // == NSDefaultRunLoopMode
        IntPtr nextSel = Sel("nextEventMatchingMask:untilDate:inMode:dequeue:");
        IntPtr sendSel = Sel("sendEvent:");
        nuint anyMask = unchecked((nuint)ulong.MaxValue);  // NSEventMaskAny

        IntPtr until = timeoutMs > 0
            ? SendDouble(Cls("NSDate"), Sel("dateWithTimeIntervalSinceNow:"), timeoutMs / 1000.0)
            : Send(Cls("NSDate"), Sel("distantPast"));

        IntPtr evt = SendEvent(app, nextSel, anyMask, until, mode, true);
        while (evt != IntPtr.Zero)
        {
            Send(app, sendSel, evt);
            evt = SendEvent(app, nextSel, anyMask, Send(Cls("NSDate"), Sel("distantPast")), mode, true);
        }
    }

    // ── Autorelease pool (one per pump / per window-build, so the message churn doesn't leak) ──
    public static IntPtr NewPool() => Send(Send(Cls("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
    public static void DrainPool(IntPtr pool) { if (pool != IntPtr.Zero) Send(pool, Sel("drain")); }
}
```

- [ ] **Step 2: Create the failing run-loop test**

Create `tests/MidiSharp.Hosting.Tests/CocoaRunLoopTests.cs`:

```csharp
using System;
using MidiSharp.Hosting.EditorHost;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// The Cocoa editor run loop in isolation: managed timers fire on pump, posted work runs on pump, and fd
/// registration is a harmless no-op (CLAP posix-fd-support is deferred on macOS). Runs on an xUnit pool thread,
/// so the run loop takes its off-main-thread <c>poll()</c> wait path — no AppKit. macOS-only.
/// </summary>
public sealed class CocoaRunLoopTests
{
    [Fact]
    public void Timer_fires_and_posted_work_runs_on_pump()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Cocoa run loop is macOS-only.");

        var loop = new CocoaRunLoop();

        var ticks = 0;
        loop.RegisterTimer(5, new object(), () => ticks++);
        for (var i = 0; i < 60 && ticks == 0; i++) loop.Pump(10);
        Assert.True(ticks > 0, "a registered timer should fire while pumping.");

        var posted = false;
        loop.Post(() => posted = true);
        loop.Pump(0);
        Assert.True(posted, "posted work should run on the next pump.");

        // RegisterFd is a no-op on macOS; it must not throw and must not break pumping.
        loop.RegisterFd(0, () => { });
        loop.Pump(0);
        loop.UnregisterFd(0);
    }

    [Fact]
    public void Unregistered_timer_stops_firing()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Cocoa run loop is macOS-only.");

        var loop = new CocoaRunLoop();
        var token = new object();
        var ticks = 0;
        loop.RegisterTimer(5, token, () => ticks++);
        for (var i = 0; i < 20; i++) loop.Pump(10);
        loop.UnregisterTimer(token);
        var after = ticks;
        for (var i = 0; i < 20; i++) loop.Pump(10);
        Assert.Equal(after, ticks);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~CocoaRunLoopTests"`
Expected: FAIL — compile error, `CocoaRunLoop` does not exist.

- [ ] **Step 4: Implement `CocoaRunLoop`**

Create `src/MidiSharp.Hosting.EditorHost/CocoaRunLoop.cs`:

```csharp
using System;
using System.Collections.Generic;
using MidiSharp.Hosting;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// The editor UI thread's run loop on macOS. The macOS analogue of <see cref="EditorRunLoop"/>/<see
/// cref="Win32RunLoop"/>: it waits up to the nearest due timer, services window events, fires due timers, and
/// runs posted work — all on the UI thread. On the main thread it drives the <c>NSApp</c> event queue
/// (<see cref="Cocoa.PumpEvents"/>); off it (unit tests) it just sleeps the timeout via <c>poll()</c>, so the
/// timer/posted logic is provable without AppKit. CLAP <c>clap.timer-support</c> and VST2 <c>effEditIdle</c>
/// map onto the timers; VST3 editors self-drive via the run loop. <c>RegisterFd</c> is a no-op: CLAP
/// <c>clap.posix-fd-support</c> integration (CFFileDescriptor) is deferred on macOS.
/// </summary>
/// <remarks>
/// Timers/posted work are mutated on the UI thread (from plugin callbacks); a lock still guards them against
/// the rare cross-thread <see cref="Post"/>.
/// </remarks>
internal sealed class CocoaRunLoop : IEditorRunLoop
{
    private sealed class Timer { public long Period; public long NextDue; public object Token = null!; public Action OnTick = null!; }

    private readonly object _lock = new();
    private readonly List<Timer> _timers = [];
    private readonly Queue<Action> _posted = [];

    // No-op on macOS: CLAP posix-fd-support integration is deferred; editors animate via timers + the NSApp loop.
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
    /// One iteration: wait up to <paramref name="maxWaitMs"/> (capped to the nearest due timer) servicing window
    /// events, then fire due timers and run posted work.
    /// </summary>
    public void Pump(int maxWaitMs)
    {
        if (maxWaitMs < 0) maxWaitMs = 0;

        int timeout;
        long now = Environment.TickCount64;
        lock (_lock)
        {
            timeout = maxWaitMs;
            foreach (Timer t in _timers) { var d = (int)Math.Max(0, t.NextDue - now); if (d < timeout) timeout = d; }
        }

        // On the main thread (the worker) drive the NSApp queue so the editor is responsive, wrapped in an
        // autorelease pool. Off it (tests) just sleep — never touch AppKit from a non-main thread.
        if (Cocoa.IsMainThread)
        {
            IntPtr pool = Cocoa.NewPool();
            try { Cocoa.PumpEvents(timeout); }
            finally { Cocoa.DrainPool(pool); }
        }
        else
        {
            Cocoa.Sleep(timeout);
        }

        // Fire due timers (snapshot so a callback can register/unregister without disturbing iteration).
        now = Environment.TickCount64;
        List<Timer> due = [];
        lock (_lock)
            foreach (Timer t in _timers)
                if (now >= t.NextDue) { due.Add(t); t.NextDue = now + t.Period; }
        foreach (Timer t in due) { try { t.OnTick(); } catch { } }

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

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~CocoaRunLoopTests"`
Expected: PASS (2 tests) on macOS; SKIP on Linux/Windows.

- [ ] **Step 6: Commit**

```bash
git add src/MidiSharp.Hosting.EditorHost/Cocoa.cs src/MidiSharp.Hosting.EditorHost/CocoaRunLoop.cs tests/MidiSharp.Hosting.Tests/CocoaRunLoopTests.cs
git commit -m "Add Cocoa editor run loop (NSApp pump + timers)"
```

---

### Task 2: Cocoa host window + platform + Select() wiring

Append the window/view/app interop to `Cocoa.cs`, add the `CocoaEditorWindow` host window (embed/resize/close/child-count), the `CocoaPlatform` factory (with the off-main-thread guard), and wire it into `EditorPlatform.Select()`. Unit-tested through the off-main-thread guard (the part xUnit *can* assert); the on-main-thread embed is Task 3.

**Files:**
- Modify: `src/MidiSharp.Hosting.EditorHost/Cocoa.cs` (append window/view/color/size/screen interop)
- Create: `src/MidiSharp.Hosting.EditorHost/CocoaEditorWindow.cs`
- Modify: `src/MidiSharp.Hosting.EditorHost/IEditorPlatform.cs:62-68`
- Test: `tests/MidiSharp.Hosting.Tests/CocoaPlatformTests.cs`

**Interfaces:**
- Consumes: `INativeEditorWindow` / `IEditorPlatform` (in `EditorHost`), `CocoaRunLoop` (Task 1), the `Cocoa` interop (Task 1 + this task).
- Produces: `internal sealed class CocoaPlatform : IEditorPlatform`; `internal sealed class CocoaEditorWindow : INativeEditorWindow` with `WindowApi => "cocoa"`, `Handle` = the content `NSView*` as `ulong`, `RunLoop` = a `CocoaRunLoop`, and `internal bool Open(string title, int width, int height)`. `EditorPlatform.Select()` returns `CocoaPlatform` on macOS.

- [ ] **Step 1: Append the window/view/color/size/screen interop to `Cocoa.cs`**

Add these members inside the `Cocoa` class in `src/MidiSharp.Hosting.EditorHost/Cocoa.cs` (before the closing brace). All struct-by-value and struct-return shapes here were spike-verified on arm64:

```csharp
    // ── Geometry (64-bit: CGFloat == double) ──
    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect { public double X, Y, W, H; public CGRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGSize { public double W, H; public CGSize(double w, double h) { W = w; H = h; } }

    // ── Messaging shapes for window/view construction (CGRect/CGSize by value; BOOL/CGRect returns) ──
    [DllImport(Objc, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SendBool(IntPtr recv, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern void SendSetBool(IntPtr recv, IntPtr sel, [MarshalAs(UnmanagedType.U1)] bool value);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern CGRect SendRect(IntPtr recv, IntPtr sel);                       // -frame
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern void SendSetFrame(IntPtr recv, IntPtr sel, CGRect frame);       // -setFrame:
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern void SendSetSize(IntPtr recv, IntPtr sel, CGSize size);         // -setContentSize:
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr SendInitView(IntPtr recv, IntPtr sel, CGRect frame);     // -initWithFrame:
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr SendInitWindow(IntPtr recv, IntPtr sel, CGRect rect, nuint styleMask, nuint backing, [MarshalAs(UnmanagedType.U1)] bool defer);

    // ── Display presence (thread-safe C function — no AppKit, callable from any thread for availability) ──
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern uint CGMainDisplayID();
    public static bool HasDisplay() => CGMainDisplayID() != 0;

    // NSWindow style mask bits and backing store.
    public const nuint NSWindowStyleMaskTitled = 1, NSWindowStyleMaskClosable = 2, NSWindowStyleMaskResizable = 8;
    public const nuint NSBackingStoreBuffered = 2;
```

- [ ] **Step 2: Create the failing platform test**

Create `tests/MidiSharp.Hosting.Tests/CocoaPlatformTests.cs`:

```csharp
using System;
using MidiSharp.Hosting.EditorHost;
using Xunit;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// The Cocoa backend at the platform level, exercising what xUnit can (it runs on a pool thread, not the main
/// thread, so it cannot create a real AppKit window — that gate is the MacEditorHarness). Verifies the backend
/// reports availability without throwing on any thread, and that it declines to create a window off the main
/// thread (the fail-safe guard that keeps the in-process/sandbox-off path from crashing on macOS). macOS-only.
/// </summary>
[Collection("EditorWindows")]
public sealed class CocoaPlatformTests
{
    [Fact]
    public void Declines_window_creation_off_the_main_thread()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Cocoa backend is macOS-only.");

        var platform = new CocoaPlatform();
        _ = platform.IsAvailable;   // must not throw on a non-main thread (uses CGMainDisplayID, not AppKit)

        // xUnit runs this on a pool thread. AppKit is main-thread-only, so the backend must return null rather
        // than build a window off the main thread — turning the in-process path into a clean no-op on macOS.
        INativeEditorWindow? window = platform.CreateWindow("off-main", 320, 240);
        Assert.Null(window);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~CocoaPlatformTests"`
Expected: FAIL — compile error, `CocoaPlatform`/`CocoaEditorWindow` do not exist.

- [ ] **Step 4: Implement `CocoaPlatform` + `CocoaEditorWindow`**

Create `src/MidiSharp.Hosting.EditorHost/CocoaEditorWindow.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.EditorHost.Cocoa;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>The macOS (Cocoa) windowing backend. arm64 only.</summary>
internal sealed class CocoaPlatform : IEditorPlatform
{
    private static bool? _available;

    // A window server must be present (false on a headless/SSH session → live gate self-skips) and the process
    // must be arm64 (we don't ship the x86_64 objc_msgSend_stret path). The probe is AppKit-free, so it's safe
    // from any thread.
    public bool IsAvailable => _available ??=
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 && HasDisplay();

    public INativeEditorWindow? CreateWindow(string title, int width, int height)
    {
        // AppKit is main-thread-only. Off the main thread (the in-process EditorWindow background thread),
        // decline cleanly so that path fails safe instead of risking an in-process crash. The sandbox worker
        // calls this on its process main thread and passes.
        if (!IsMainThread) return null;
        EnsureApp();
        var w = new CocoaEditorWindow();
        return w.Open(title, width, height) ? w : null;
    }
}

/// <summary>
/// A top-level NSWindow that hosts an embedded plugin editor: it owns the window and its content NSView (the
/// view the plugin parents into), and an <see cref="CocoaRunLoop"/> that services the NSApp event queue
/// alongside the plugin's timers. The macOS analogue of <see cref="X11EditorWindow"/>/<see
/// cref="Win32EditorWindow"/>; all Cocoa detail is confined to this class (and <see cref="Cocoa"/>). Created and
/// pumped on the process main thread.
/// </summary>
internal sealed class CocoaEditorWindow : INativeEditorWindow
{
    private readonly CocoaRunLoop _runLoop = new();
    private IntPtr _window;
    private IntPtr _content;   // the content NSView; what the plugin parents into and what Handle exposes
    private bool _opened;

    public string WindowApi => "cocoa";
    public ulong Handle => (ulong)_content;
    public IEditorRunLoop RunLoop => _runLoop;

    // The user closing the title-bar button orders the window out → not visible. (setReleasedWhenClosed:NO keeps
    // the object alive for Dispose.) Before Map() there is nothing to close.
    public bool ShouldClose => _opened && !SendBool(_window, Sel("isVisible"));

    public uint EmbeddedChildCount
    {
        get
        {
            if (_content == IntPtr.Zero) return 0;
            IntPtr subs = Send(_content, Sel("subviews"));
            return subs == IntPtr.Zero ? 0 : (uint)SendUInt(subs, Sel("count"));
        }
    }

    internal bool Open(string title, int width, int height)
    {
        IntPtr pool = NewPool();
        try
        {
            nuint mask = NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskResizable;
            IntPtr win = Send(Cls("NSWindow"), Sel("alloc"));
            win = SendInitWindow(win, Sel("initWithContentRect:styleMask:backing:defer:"),
                new CGRect(0, 0, width, height), mask, NSBackingStoreBuffered, false);
            if (win == IntPtr.Zero) return false;
            _window = win;

            SendSetBool(win, Sel("setReleasedWhenClosed:"), false);   // we control teardown in Dispose
            Send(win, Sel("setTitle:"), NSString(title));
            // Background BLACK (not white): the embedded plugin owns the pixels, but a white parent shows through
            // compositing seams as bright lines behind a dark editor (same fix as the X11/Win32 backends).
            Send(win, Sel("setBackgroundColor:"), Send(Cls("NSColor"), Sel("blackColor")));

            _content = Send(win, Sel("contentView"));   // the NSView the plugin will add its editor under
            return _content != IntPtr.Zero;
        }
        finally { DrainPool(pool); }
    }

    public void Resize(int width, int height)
    {
        if (_window == IntPtr.Zero || width <= 0 || height <= 0) return;
        IntPtr pool = NewPool();
        try
        {
            SendSetSize(_window, Sel("setContentSize:"), new CGSize(width, height));
            IntPtr child = FirstSubview();
            if (child != IntPtr.Zero) SendSetFrame(child, Sel("setFrame:"), new CGRect(0, 0, width, height));
        }
        finally { DrainPool(pool); }
    }

    public void Map()
    {
        Send(_window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        SendSetBool(App, Sel("activateIgnoringOtherApps:"), true);
        _opened = true;
    }

    public void CompleteEmbed() { }   // no XEMBED-style handshake on Cocoa

    public void PumpOnce(int maxWaitMs) => _runLoop.Pump(maxWaitMs);

    private IntPtr FirstSubview()
    {
        IntPtr subs = Send(_content, Sel("subviews"));
        if (subs == IntPtr.Zero || SendUInt(subs, Sel("count")) == 0) return IntPtr.Zero;
        return Send(subs, Sel("firstObject"));
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero)
        {
            Send(_window, Sel("orderOut:"), IntPtr.Zero);
            Send(_window, Sel("close"));
            Send(_window, Sel("release"));   // balance the +1 from alloc (setReleasedWhenClosed:NO)
            _window = IntPtr.Zero;
        }
        _content = IntPtr.Zero;
        _opened = false;
    }
}
```

- [ ] **Step 5: Wire the backend into `EditorPlatform.Select()`**

Edit `src/MidiSharp.Hosting.EditorHost/IEditorPlatform.cs`. Replace the body of `Select()` (lines 62-68):

```csharp
    private static IEditorPlatform Select()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) return new X11Platform();
        if (OperatingSystem.IsWindows()) return new Win32Platform();
        // macOS (Cocoa) backend slots in here.
        return new UnsupportedPlatform();
    }
```

with:

```csharp
    private static IEditorPlatform Select()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) return new X11Platform();
        if (OperatingSystem.IsWindows()) return new Win32Platform();
        if (OperatingSystem.IsMacOS()) return new CocoaPlatform();
        return new UnsupportedPlatform();
    }
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/MidiSharp.Hosting.Tests --filter "FullyQualifiedName~CocoaPlatformTests"`
Expected: PASS on macOS; SKIP on Linux/Windows.

- [ ] **Step 7: Commit**

```bash
git add src/MidiSharp.Hosting.EditorHost/Cocoa.cs src/MidiSharp.Hosting.EditorHost/CocoaEditorWindow.cs src/MidiSharp.Hosting.EditorHost/IEditorPlatform.cs tests/MidiSharp.Hosting.Tests/CocoaPlatformTests.cs
git commit -m "Add Cocoa host window + platform, wire into EditorPlatform.Select()"
```

---

### Task 3: End-to-end embed via a standalone main-thread harness (acceptance gate)

Prove the full `EditorSession` embed sequence works on macOS with a managed `IPluginGui` that creates a real child `NSView` — no native plugin or toolchain. Because AppKit is main-thread-only and xUnit runs on pool threads, this gate is a **console program whose `Main` is the process main thread** (spec §5.3, §10 item 2). The macOS analogue of `Win32EditorWindowTests`, run via `dotnet run`.

**Files:**
- Create: `tests/MidiSharp.Hosting.MacEditorHarness/MacEditorHarness.csproj`
- Create: `tests/MidiSharp.Hosting.MacEditorHarness/FakeCocoaEditorGui.cs`
- Create: `tests/MidiSharp.Hosting.MacEditorHarness/Program.cs`
- Modify: `MidiSharp.slnx` (add the project under `/Tests/`)

**Interfaces:**
- Consumes: `IPluginGui` (in `MidiSharp.Hosting`), `EditorSession.Open(IPluginGui?, string)` / `PumpOnce` / `EmbeddedChildCount` / `Close` (in `EditorHost`).
- Produces: a console exe printing `PASS`/`FAIL`/`SKIP` and returning 0 (pass/skip) or 1 (fail).

- [ ] **Step 1: Create the harness project**

Create `tests/MidiSharp.Hosting.MacEditorHarness/MacEditorHarness.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    macOS Cocoa editor-host acceptance gate. AppKit is main-thread-only and xUnit runs tests on pool threads,
    so the window/embed proof lives here: a console whose Main (thread 0) opens an EditorSession and asserts a
    child NSView embeds. Run on macOS via dotnet run; PASS/SKIP → exit 0, FAIL → exit 1.
  -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\MidiSharp.Hosting.EditorHost\MidiSharp.Hosting.EditorHost.csproj" />
    <ProjectReference Include="..\..\src\MidiSharp.Hosting\MidiSharp.Hosting.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the managed fake editor**

Create `tests/MidiSharp.Hosting.MacEditorHarness/FakeCocoaEditorGui.cs`. It declares its own minimal objc interop (mirroring how the Win32 `FakeEditorGui` declared its own `CreateWindowExW`), so it doesn't need `EditorHost` internals:

```csharp
using System;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;

namespace MidiSharp.Hosting.MacEditorHarness;

/// <summary>
/// A managed <see cref="IPluginGui"/> standing in for a native plugin editor on macOS: on
/// <see cref="SetParent"/> it allocates a real child NSView and adds it under the host's content view, exactly
/// as a native editor parents its view. Lets the Cocoa editor backend be verified end-to-end with no native
/// plugin or toolchain — the GUI analogue of the Win32 <c>FakeEditorGui</c>.
/// </summary>
internal sealed class FakeCocoaEditorGui : IPluginGui
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    [DllImport(Objc)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string n);
    [DllImport(Objc)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string n);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendInitView(IntPtr r, IntPtr s, CGRect frame);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect { public double X, Y, W, H; public CGRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; } }

    private readonly int _w, _h;
    private IntPtr _child;

    public FakeCocoaEditorGui(int width = 320, int height = 240) { _w = width; _h = height; }

    public bool HasEditor => true;
    public bool IsApiSupported(string windowApi, bool floating) => windowApi == "cocoa";
    public bool Create(string windowApi, bool floating) => windowApi == "cocoa";
    public bool SetScale(double scale) => true;
    public bool TryGetSize(out int width, out int height) { width = _w; height = _h; return true; }

    public bool SetParent(string windowApi, ulong windowHandle)
    {
        if (windowApi != "cocoa") return false;
        IntPtr view = Send(objc_getClass("NSView"), sel_registerName("alloc"));
        view = SendInitView(view, sel_registerName("initWithFrame:"), new CGRect(0, 0, _w, _h));
        if (view == IntPtr.Zero) return false;
        Send((IntPtr)windowHandle, sel_registerName("addSubview:"), view);   // handle = host content NSView*
        _child = view;
        return true;
    }

    public bool Show() => true;
    public bool Hide() => true;

    public void Destroy()
    {
        if (_child != IntPtr.Zero) { Send(_child, sel_registerName("removeFromSuperview")); _child = IntPtr.Zero; }
    }
}
```

- [ ] **Step 3: Create the harness entry point**

Create `tests/MidiSharp.Hosting.MacEditorHarness/Program.cs`:

```csharp
using System;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.MacEditorHarness;

// Runs on the process main thread (thread 0), which AppKit requires and xUnit cannot provide. Opens an
// EditorSession with a fake cocoa editor and confirms the host parented a child NSView, then resizes and closes.
// PASS/SKIP → exit 0 (CI-safe when headless); FAIL → exit 1.

if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP: Cocoa editor backend is macOS-only."); return 0; }
if (!EditorPlatform.Current.IsAvailable) { Console.WriteLine("SKIP: no window server (headless session)."); return 0; }

var gui = new FakeCocoaEditorGui(320, 240);
using EditorSession? session = EditorSession.Open(gui, "MidiSharp macOS editor harness");
if (session is not { IsOpen: true })
{
    Console.WriteLine($"FAIL: editor session did not open (error: {session?.Error}).");
    return 1;
}

uint children = 0;
for (var i = 0; i < 40 && children == 0; i++) { children = session.EmbeddedChildCount; if (children == 0) session.PumpOnce(25); }
if (children < 1)
{
    Console.WriteLine("FAIL: the fake editor did not embed a child NSView into the host window.");
    return 1;
}

session.PumpOnce(25);
session.Close();

Console.WriteLine($"PASS: embedded {children} child NSView(s) and closed cleanly.");
return 0;
```

> The harness uses only `EditorSession`'s public surface (`Open`/`IsOpen`/`Error`/`EmbeddedChildCount`/`PumpOnce`/`Close`/`ShouldClose`). `EditorSession` has no public `Resize` — the embed sequence resizes the window internally (`OpenInternal` calls `_window.Resize` after `Create` and after `Show`), so `CocoaEditorWindow.Resize` is already exercised by opening; the gate asserts the embed (child `NSView` count ≥ 1) and a clean close.

- [ ] **Step 4: Register the harness project in the solution**

Edit `MidiSharp.slnx` — add inside the `<Folder Name="/Tests/">` element, alongside the other test projects:

```xml
    <Project Path="tests/MidiSharp.Hosting.MacEditorHarness/MacEditorHarness.csproj" />
```

- [ ] **Step 5: Build the harness and run the gate**

```bash
dotnet build tests/MidiSharp.Hosting.MacEditorHarness/MacEditorHarness.csproj -c Debug
dotnet run --project tests/MidiSharp.Hosting.MacEditorHarness -c Debug
```
Expected on this Mac (arm64, window server present): `PASS: embedded 1 child NSView(s), resized, and closed.` exit 0. On a headless session: `SKIP …` exit 0.

- [ ] **Step 6: Run the whole Hosting suite to confirm no regressions**

Run: `dotnet test tests/MidiSharp.Hosting.Tests`
Expected: all pass; Linux/X11- and Windows/Win32-only tests SKIP, the Cocoa run-loop/platform tests PASS, no failures.

- [ ] **Step 7: Build the full solution clean**

Run: `dotnet build MidiSharp.slnx -c Debug`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 8: Commit**

```bash
git add tests/MidiSharp.Hosting.MacEditorHarness MidiSharp.slnx
git commit -m "Verify Cocoa editor backend embeds a child NSView end-to-end (main-thread harness)"
```

---

## Self-Review

**Spec coverage (§5.1, §5.3, §7, §9, §10 items 1–2):**
- `Cocoa.cs` interop slice (messaging, caches, NSApp bootstrap, run-loop primitives, geometry, display probe) → Task 1 + Task 2. ✓
- `CocoaRunLoop : IEditorRunLoop` (timers + posted + NSApp pump; off-main `poll()` fallback; RegisterFd no-op) → Task 1. ✓
- `CocoaEditorWindow : INativeEditorWindow` (WindowApi/Handle/Resize/Map/CompleteEmbed/EmbeddedChildCount/ShouldClose/PumpOnce/Dispose) → Task 2. ✓
- `CocoaPlatform` + the off-main-thread guard + `Select()` wiring → Task 2. ✓ (the fail-safe for the in-process/sandbox-off path, §5.3.)
- Black background, `setReleasedWhenClosed:NO`, autorelease pool per pump/build, `isVisible` close detection, Accessory activation policy (§9) → Tasks 1–2. ✓
- Main-thread embed gate as a standalone harness (acceptance gate item 2, since xUnit can't host AppKit) → Task 3. ✓
- Out of scope here (Plan B): per-format adapter changes (`"cocoa"`/`"NSView"`), native `.clap`/`.vst`/`.vst3` fixtures, per-format live checks.

**Placeholder scan:** No TBD/TODO; every code step shows complete code; every run step shows the command (via `dotnet`) and expected result. The one conditional is Step 3's note to drop `session.Resize(...)` if `EditorSession` has no public `Resize` — explicitly flagged with the verification. ✓

**Type consistency:** `CocoaRunLoop.Pump(int)` defined in Task 1, called by `CocoaEditorWindow.PumpOnce` in Task 2. `CocoaEditorWindow.Open(string,int,int)` defined and called by `CocoaPlatform.CreateWindow` in Task 2. `INativeEditorWindow` members match `IEditorPlatform.cs`. `IPluginGui` members in `FakeCocoaEditorGui` (Task 3) match the non-default members of `IPluginGui.cs` (`BindRunLoop`/`Idle` use interface defaults). All `Cocoa` members used by `CocoaEditorWindow`/`CocoaRunLoop` are declared across Tasks 1–2 (`Send` overloads, `SendUInt`, `SendBool`, `SendSetBool`, `SendRect`/`SendSetFrame`/`SendSetSize`/`SendInitView`/`SendInitWindow`, `Cls`/`Sel`/`NSString`/`App`/`EnsureApp`/`IsMainThread`/`Sleep`/`PumpEvents`/`NewPool`/`DrainPool`/`HasDisplay` + the style/backing constants). ✓

**Interop grounding:** the `objc_msgSend` shapes used here (`CGRect` by value in `initWithContentRect:`/`initWithFrame:`, `CGRect` return in `-frame`, `nuint` return in `-count`, UTF8 string arg in `stringWithUTF8String:`, the `NSApp` event pump, `addSubview:` embedding) were spike-verified on this arm64 Mac on 2026-06-22. `CGSize` by value, `double` by value (`dateWithTimeIntervalSinceNow:`), and `BOOL` args are strictly simpler cases of the same AAPCS64 register rules.

**Threading note for the implementer:** `CocoaRunLoopTests`/`CocoaPlatformTests` run on xUnit pool threads (non-main) — that is intentional: they exercise the off-main-thread `poll()` wait and the off-main-thread `CreateWindow` guard. The real on-main-thread window/embed lives in the `MacEditorHarness` (`dotnet run`), never in xUnit. Do not try to "fix" the platform test to create a real window — it can't, by design.
