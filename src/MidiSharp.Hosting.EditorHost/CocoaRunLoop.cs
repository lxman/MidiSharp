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
