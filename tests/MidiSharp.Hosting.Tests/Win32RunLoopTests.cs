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
        Assert.True(ticks > 0, "timer should have fired before unregister.");
        loop.UnregisterTimer(token);
        int after = ticks;
        for (var i = 0; i < 10; i++) loop.Pump(10);
        Assert.Equal(after, ticks);
    }
}
