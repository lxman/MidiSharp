using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.EditorHost.Linux;

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
    [DllImport(Lib)] public static extern int XConnectionNumber(IntPtr display);   // the display's socket fd

    [DllImport(Lib)] public static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(Lib)] public static extern int XSetWMProtocols(IntPtr display, ulong w, ref IntPtr protocols, int count);
    [DllImport(Lib)] public static extern int XSendEvent(IntPtr display, ulong w, bool propagate, long eventMask, IntPtr eventSend);

    // XEMBED: after reparenting a client into an embedder window, the host must notify the client so it can
    // proceed (some plugins' show() blocks until this arrives). See the freedesktop XEMBED spec.
    private const int XEMBED_EMBEDDED_NOTIFY = 0;
    private const int ClientMessageType = 33;

    /// <summary>Send XEMBED_EMBEDDED_NOTIFY from <paramref name="embedder"/> to the embedded <paramref name="client"/>.</summary>
    public static void SendXEmbedNotify(IntPtr display, ulong embedder, ulong client)
    {
        IntPtr atom = XInternAtom(display, "_XEMBED", false);
        IntPtr ev = Marshal.AllocHGlobal(96);
        try
        {
            for (var i = 0; i < 96; i++) Marshal.WriteByte(ev, i, 0);
            Marshal.WriteInt32(ev, 0, ClientMessageType);   // type
            Marshal.WriteInt64(ev, 32, (long)client);        // window (the embedded client)
            Marshal.WriteInt64(ev, 40, atom.ToInt64());      // message_type = _XEMBED
            Marshal.WriteInt32(ev, 48, 32);                  // format
            // data.l @56: [0]=time(0) [1]=XEMBED_EMBEDDED_NOTIFY [2]=detail(0) [3]=embedder [4]=version(0)
            Marshal.WriteInt64(ev, 56 + 8 * 1, XEMBED_EMBEDDED_NOTIFY);
            Marshal.WriteInt64(ev, 56 + 8 * 3, (long)embedder);
            XSendEvent(display, client, false, 0, ev);
            XFlush(display);
        }
        finally { Marshal.FreeHGlobal(ev); }
    }

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
        if (XQueryTree(display, w, out _, out _, out IntPtr children, out uint n) == 0) return 0;
        if (children != IntPtr.Zero) XFree(children);
        return n;
    }

    /// <summary>The first direct child window of <paramref name="w"/> (the plugin's embedded editor), or 0.</summary>
    public static ulong FirstChild(IntPtr display, ulong w)
    {
        if (XQueryTree(display, w, out _, out _, out IntPtr children, out uint n) == 0 || n == 0 || children == IntPtr.Zero)
        {
            if (children != IntPtr.Zero) XFree(children);
            return 0;
        }
        var child = (ulong)Marshal.ReadInt64(children);   // children[0]
        XFree(children);
        return child;
    }
}
