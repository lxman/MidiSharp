using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
// ReSharper disable InconsistentNaming

namespace MidiSharp.Hosting.EditorHost.MacArm;

/// <summary>
/// The slice of the Objective-C runtime + AppKit needed to host a plugin editor on macOS: create an NSWindow
/// with a content NSView, embed the plugin's NSView under it, show/resize it, pump the NSApp event queue, and
/// tear it down. The macOS analogue of <see cref="Linux.X11"/>/<see cref="Windows.Win32"/>; all libobjc/AppKit P/Invoke lives
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
}
