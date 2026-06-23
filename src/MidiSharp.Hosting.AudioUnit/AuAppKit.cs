using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.AudioUnit;

/// <summary>Geometry structs CoreGraphics/AppKit pass by value (arm64: returned/passed directly, no <c>_stret</c>).</summary>
[StructLayout(LayoutKind.Sequential)] public struct CGSize { public double Width, Height; }
[StructLayout(LayoutKind.Sequential)] public struct CGRect { public double X, Y, Width, Height; }

/// <summary>
/// The minimal Objective-C / AppKit slice the AU editor needs to vend and embed an <c>NSView</c>: instantiate
/// an AU's Cocoa view factory (<c>AUCocoaUIBase</c>) or the generic <c>AUGenericView</c>, add it under a host
/// view, and read/toggle it. arm64-only; <c>objc_msgSend</c> is uniform there (no <c>_stret</c>), matching the
/// EditorHost Cocoa backend. AppKit + CoreAudioKit are loaded so <c>NSView</c>/<c>AUGenericView</c> resolve.
/// </summary>
internal static unsafe class AuAppKit
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Objc, CharSet = CharSet.Ansi)] public static extern IntPtr objc_getClass(string name);
    [DllImport(Objc, CharSet = CharSet.Ansi)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendArg(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendArgSize(IntPtr self, IntPtr sel, IntPtr a, CGSize size);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void SendSetBool(IntPtr self, IntPtr sel, byte b);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr self, IntPtr sel);

    private static readonly IntPtr SelAlloc = sel_registerName("alloc");
    private static readonly IntPtr SelInit = sel_registerName("init");
    private static readonly IntPtr SelRetain = sel_registerName("retain");
    private static readonly IntPtr SelRelease = sel_registerName("release");
    private static readonly IntPtr SelFrame = sel_registerName("frame");
    private static readonly IntPtr SelAddSubview = sel_registerName("addSubview:");
    private static readonly IntPtr SelRemoveFromSuperview = sel_registerName("removeFromSuperview");
    private static readonly IntPtr SelSetHidden = sel_registerName("setHidden:");
    private static readonly IntPtr SelInitWithAudioUnit = sel_registerName("initWithAudioUnit:");
    private static readonly IntPtr SelUiViewForAudioUnit = sel_registerName("uiViewForAudioUnit:withSize:");
    private static readonly IntPtr SelWindow = sel_registerName("window");
    private static readonly IntPtr SelSetBackgroundColor = sel_registerName("setBackgroundColor:");
    private static readonly IntPtr SelWindowBackgroundColor = sel_registerName("windowBackgroundColor");

    private static bool _coreAudioKitLoaded;

    static AuAppKit()
    {
        // NSView (AppKit) classes must resolve via objc_getClass for view ops.
        NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out _);
    }

    /// <summary>The <c>AUGenericView</c> class, loading CoreAudioKit on first use. Lazy so the common
    /// custom-view path (which doesn't need CoreAudioKit) avoids its duplicate-class warnings against AU view
    /// bundles.</summary>
    public static IntPtr GenericViewClass()
    {
        if (!_coreAudioKitLoaded)
        {
            NativeLibrary.TryLoad("/System/Library/Frameworks/CoreAudioKit.framework/CoreAudioKit", out _);
            _coreAudioKitLoaded = true;
        }
        return objc_getClass("AUGenericView");
    }

    /// <summary><c>[[cls alloc] init]</c>.</summary>
    public static IntPtr New(IntPtr cls) => Send(Send(cls, SelAlloc), SelInit);

    /// <summary><c>[[cls alloc] initWithAudioUnit:au]</c> — the AUGenericView fallback.</summary>
    public static IntPtr NewWithAudioUnit(IntPtr cls, IntPtr au) => SendArg(Send(cls, SelAlloc), SelInitWithAudioUnit, au);

    /// <summary><c>[factory uiViewForAudioUnit:au withSize:size]</c> — the custom AUCocoaUIBase view.</summary>
    public static IntPtr UiViewForAudioUnit(IntPtr factory, IntPtr au, CGSize size) => SendArgSize(factory, SelUiViewForAudioUnit, au, size);

    public static IntPtr Retain(IntPtr obj) => Send(obj, SelRetain);
    public static void Release(IntPtr obj) { if (obj != IntPtr.Zero) Send(obj, SelRelease); }
    public static void AddSubview(IntPtr parent, IntPtr child) => SendArg(parent, SelAddSubview, child);
    public static void RemoveFromSuperview(IntPtr view) => Send(view, SelRemoveFromSuperview);
    public static void SetHidden(IntPtr view, bool hidden) => SendSetBool(view, SelSetHidden, (byte)(hidden ? 1 : 0));
    public static CGRect Frame(IntPtr view) => SendRect(view, SelFrame);

    public static IntPtr WindowOf(IntPtr view) => Send(view, SelWindow);

    /// <summary>Set an NSWindow's background to the appearance-aware system <c>windowBackgroundColor</c> — neutral
    /// contrast behind a transparent AU view (some draw dark controls that vanish on the default black surround).</summary>
    public static void SetNeutralWindowBackground(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        IntPtr color = Send(objc_getClass("NSColor"), SelWindowBackgroundColor);
        SendArg(window, SelSetBackgroundColor, color);
    }
}
