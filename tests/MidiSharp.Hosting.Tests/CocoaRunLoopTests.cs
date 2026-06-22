using System;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.EditorHost.MacArm;
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
