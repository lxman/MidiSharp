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
