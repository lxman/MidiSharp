using System;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.EditorHost.MacArm.Cocoa;

namespace MidiSharp.Hosting.EditorHost.MacArm;

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
/// alongside the plugin's timers. The macOS analogue of <see cref="Linux.X11EditorWindow"/>/<see
/// cref="Windows.Win32EditorWindow"/>; all Cocoa detail is confined to this class (and <see cref="Cocoa"/>). Created and
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
