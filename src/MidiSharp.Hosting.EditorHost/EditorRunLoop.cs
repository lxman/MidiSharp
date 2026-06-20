using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// The editor UI thread's run loop. A real plugin editor doesn't draw once and stop — it registers its
/// windowing-system connection (a file descriptor) and animation timers with the host and relies on the
/// host to poll them and call it back. This drives those: each pump <c>poll()</c>s the host window's X11 fd
/// plus every plugin-registered fd (with a timeout set by the nearest due timer), then on the UI thread it
/// drains the host's window events, calls back any ready plugin fds, fires due timers, and runs posted work.
/// </summary>
/// <remarks>
/// Registrations and unregistrations happen on the UI thread (from inside plugin callbacks), so the lists
/// are only mutated there; a lock still guards them against the rare cross-thread <see cref="Post"/>.
/// </remarks>
internal sealed class EditorRunLoop : IEditorRunLoop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int Fd; public short Events; public short Revents; }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);

    private const short POLLIN = 0x001;

    private sealed class Timer { public long Period; public long NextDue; public object Token = null!; public Action OnTick = null!; }

    private readonly object _lock = new();
    private readonly List<(int fd, Action onReady)> _fds = [];
    private readonly List<Timer> _timers = [];
    private readonly Queue<Action> _posted = [];

    public void RegisterFd(int fd, Action onReady)
    {
        lock (_lock) { _fds.RemoveAll(e => e.fd == fd); _fds.Add((fd, onReady)); }
    }

    public void UnregisterFd(int fd)
    {
        lock (_lock) _fds.RemoveAll(e => e.fd == fd);
    }

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

    /// <summary>Queue an action to run on the UI thread on the next pump (e.g. a param set while the editor is open).</summary>
    public void Post(Action action)
    {
        lock (_lock) _posted.Enqueue(action);
    }

    /// <summary>
    /// One iteration: poll the host fd + plugin fds (timeout = nearest due timer, capped), then drain the
    /// host's window events, the ready plugin fds, the due timers, and any posted work — all on this thread.
    /// </summary>
    public void Pump(int hostFd, Action drainHostEvents, int maxWaitMs)
    {
        PollFd[] set;
        Action[] readyActions;
        int timeout;
        var now = Environment.TickCount64;
        lock (_lock)
        {
            set = new PollFd[1 + _fds.Count];
            readyActions = new Action[_fds.Count];
            set[0] = new PollFd { Fd = hostFd, Events = POLLIN };
            for (var i = 0; i < _fds.Count; i++) { set[i + 1] = new PollFd { Fd = _fds[i].fd, Events = POLLIN }; readyActions[i] = _fds[i].onReady; }

            timeout = maxWaitMs;
            foreach (var t in _timers) { var d = (int)Math.Max(0, t.NextDue - now); if (d < timeout) timeout = d; }
        }

        poll(set, (nuint)set.Length, timeout);

        if ((set[0].Revents & POLLIN) != 0) drainHostEvents();
        for (var i = 0; i < readyActions.Length; i++)
            if ((set[i + 1].Revents & POLLIN) != 0) { try { readyActions[i](); } catch { } }

        // Fire due timers (snapshot so a timer callback can register/unregister without disturbing iteration).
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
