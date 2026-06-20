using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.EditorHost;

/// <summary>
/// The slice of Xlib needed to host a plugin editor: open a display, create a top-level window of a given
/// size to embed the editor into, map/unmap it, pump its events (so resize/close work), and tear it down.
/// X11 is the embedding API CLAP/VST2/VST3 use on Linux (Wayland sessions run these through Xwayland). All
/// of this is main-thread work, off the audio path.
/// </summary>
internal static class X11
{
    private const string Lib = "libX11.so.6";

    [DllImport(Lib)] public static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(Lib)] public static extern ulong XDefaultRootWindow(IntPtr display);
    [DllImport(Lib)] public static extern int XDefaultScreen(IntPtr display);
    [DllImport(Lib)] public static extern ulong XWhitePixel(IntPtr display, int screen);
    [DllImport(Lib)] public static extern ulong XBlackPixel(IntPtr display, int screen);

    [DllImport(Lib)]
    public static extern ulong XCreateSimpleWindow(IntPtr display, ulong parent, int x, int y,
        uint width, uint height, uint borderWidth, ulong border, ulong background);

    [DllImport(Lib)] public static extern int XDestroyWindow(IntPtr display, ulong w);
    [DllImport(Lib)] public static extern int XMapWindow(IntPtr display, ulong w);
    [DllImport(Lib)] public static extern int XUnmapWindow(IntPtr display, ulong w);
    [DllImport(Lib)] public static extern int XStoreName(IntPtr display, ulong w, string name);
    [DllImport(Lib)] public static extern int XSelectInput(IntPtr display, ulong w, long eventMask);
    [DllImport(Lib)] public static extern int XResizeWindow(IntPtr display, ulong w, uint width, uint height);
    [DllImport(Lib)] public static extern int XFlush(IntPtr display);
    [DllImport(Lib)] public static extern int XSync(IntPtr display, bool discard);
    [DllImport(Lib)] public static extern int XPending(IntPtr display);
    [DllImport(Lib)] public static extern int XNextEvent(IntPtr display, IntPtr eventReturn);

    [DllImport(Lib)] public static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(Lib)] public static extern int XSetWMProtocols(IntPtr display, ulong w, ref IntPtr protocols, int count);

    // XQueryTree — used to verify an embed: after set_parent the plugin's window appears as a child.
    [DllImport(Lib)]
    public static extern int XQueryTree(IntPtr display, ulong w, out ulong root, out ulong parent,
        out IntPtr children, out uint nchildren);
    [DllImport(Lib)] public static extern int XFree(IntPtr data);

    public const long StructureNotifyMask = 1L << 17;
    public const long SubstructureNotifyMask = 1L << 19;
    public const long ExposureMask = 1L << 15;
    public const int ClientMessage = 33;

    /// <summary>Count the direct child windows of <paramref name="w"/> (the embedded editor shows up here).</summary>
    public static uint ChildCount(IntPtr display, ulong w)
    {
        if (XQueryTree(display, w, out _, out _, out var children, out var n) == 0) return 0;
        if (children != IntPtr.Zero) XFree(children);
        return n;
    }
}
