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
