using System;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;

namespace MidiSharp.Hosting.Tests;

/// <summary>
/// A managed <see cref="IPluginGui"/> standing in for a native plugin editor on Windows: on
/// <see cref="SetParent"/> it creates a real WS_CHILD "STATIC" window under the host HWND, exactly as a
/// native editor parents its window. Lets the Win32 editor backend (window, run loop, embed sequence) be
/// verified end-to-end with no native plugin or toolchain — the GUI analogue of <c>FakeGainPlugin</c>.
/// </summary>
internal sealed class FakeEditorGui : IPluginGui
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;

    private readonly int _w;
    private readonly int _h;
    private IntPtr _child;

    public FakeEditorGui(int width = 320, int height = 240) { _w = width; _h = height; }

    public bool HasEditor => true;
    public bool IsApiSupported(string windowApi, bool floating) => windowApi == "win32";
    public bool Create(string windowApi, bool floating) => windowApi == "win32";
    public bool SetScale(double scale) => true;

    public bool TryGetSize(out int width, out int height) { width = _w; height = _h; return true; }

    public bool SetParent(string windowApi, ulong windowHandle)
    {
        if (windowApi != "win32") return false;
        // A built-in "STATIC" control class needs no registration; this is the embedded child the host counts.
        _child = CreateWindowExW(0, "STATIC", null, WS_CHILD | WS_VISIBLE, 0, 0, _w, _h,
            (IntPtr)windowHandle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return _child != IntPtr.Zero;
    }

    public bool Show() => true;
    public bool Hide() => true;

    public void Destroy()
    {
        if (_child != IntPtr.Zero) { DestroyWindow(_child); _child = IntPtr.Zero; }
    }
}
