using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.AudioUnit;

/// <summary>
/// The slice of CoreFoundation the AU adapter needs: turning a <c>CFStringRef</c> into a managed string, and
/// parsing a property list (a <c>.component</c> bundle's <c>Info.plist</c>) without loading any plugin code.
/// Grows for state (Task 5, <c>CFPropertyList</c> round-trip) and the editor (Plan C, CFBundle/CFURL).
/// </summary>
internal static unsafe class CoreFoundation
{
    private const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;   // kCFStringEncodingUTF8
    private const nint BinaryPlist = 200;   // kCFPropertyListBinaryFormat_v1_0

    [DllImport(Lib)] public static extern void CFRelease(IntPtr cf);
    [DllImport(Lib)] private static extern byte CFStringGetCString(IntPtr str, byte* buffer, nint bufferSize, uint encoding);
    [DllImport(Lib)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, byte* cStr, uint encoding);
    [DllImport(Lib)] private static extern IntPtr CFDataCreate(IntPtr alloc, byte* bytes, nint length);
    [DllImport(Lib)] private static extern IntPtr CFPropertyListCreateWithData(IntPtr alloc, IntPtr data, nuint options, out nint format, out IntPtr error);
    [DllImport(Lib)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(Lib)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(Lib)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(Lib)] private static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(Lib)] private static extern nuint CFStringGetTypeID();
    [DllImport(Lib)] private static extern nuint CFArrayGetTypeID();
    [DllImport(Lib)] private static extern nuint CFDictionaryGetTypeID();
    [DllImport(Lib)] private static extern IntPtr CFPropertyListCreateData(IntPtr alloc, IntPtr plist, nint format, nuint options, out IntPtr error);
    [DllImport(Lib)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(Lib)] private static extern byte* CFDataGetBytePtr(IntPtr data);

    /// <summary>Copy a <c>CFStringRef</c> into a managed string. Returns empty for null, a non-string object
    /// (plist values may be numbers/data — calling CFStringGetCString on those traps), or conversion failure.</summary>
    public static string ToManaged(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero || CFGetTypeID(cfString) != CFStringGetTypeID()) return "";
        Span<byte> buf = stackalloc byte[512];
        fixed (byte* p = buf)
            return CFStringGetCString(cfString, p, buf.Length, Utf8) != 0 ? Marshal.PtrToStringUTF8((IntPtr)p) ?? "" : "";
    }

    /// <summary>Parse a property-list blob (e.g. an <c>Info.plist</c>) into a CF object the caller must
    /// <see cref="CFRelease"/>. Returns <see cref="IntPtr.Zero"/> on malformed input.</summary>
    public static IntPtr CreatePropertyList(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* p = bytes)
        {
            IntPtr data = CFDataCreate(IntPtr.Zero, p, bytes.Length);
            if (data == IntPtr.Zero) return IntPtr.Zero;
            IntPtr plist = CFPropertyListCreateWithData(IntPtr.Zero, data, 0, out _, out IntPtr error);
            CFRelease(data);
            if (error != IntPtr.Zero) CFRelease(error);
            return plist;
        }
    }

    public static bool IsDictionary(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == CFDictionaryGetTypeID();
    public static bool IsArray(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == CFArrayGetTypeID();

    /// <summary>Serialize a property list (e.g. a unit's <c>ClassInfo</c> dictionary) to a binary-plist byte
    /// array. Empty on failure.</summary>
    public static byte[] ToData(IntPtr plist)
    {
        if (plist == IntPtr.Zero) return [];
        IntPtr data = CFPropertyListCreateData(IntPtr.Zero, plist, BinaryPlist, 0, out IntPtr error);
        if (error != IntPtr.Zero) CFRelease(error);
        if (data == IntPtr.Zero) return [];
        try
        {
            var len = (int)CFDataGetLength(data);
            return new ReadOnlySpan<byte>(CFDataGetBytePtr(data), len).ToArray();
        }
        finally { CFRelease(data); }
    }

    /// <summary>Look up a value in a <c>CFDictionary</c> by a string key (borrowed reference — do not release).</summary>
    public static IntPtr DictGet(IntPtr dict, string key)
    {
        if (!IsDictionary(dict)) return IntPtr.Zero;
        byte[] k = System.Text.Encoding.UTF8.GetBytes(key + '\0');
        IntPtr cfKey;
        fixed (byte* kp = k) cfKey = CFStringCreateWithCString(IntPtr.Zero, kp, Utf8);
        if (cfKey == IntPtr.Zero) return IntPtr.Zero;
        try { return CFDictionaryGetValue(dict, cfKey); }
        finally { CFRelease(cfKey); }
    }

    /// <summary>A dictionary's string value for a key (empty when absent or not a string).</summary>
    public static string DictGetString(IntPtr dict, string key) => ToManaged(DictGet(dict, key));

    public static nint ArrayCount(IntPtr array) => IsArray(array) ? CFArrayGetCount(array) : 0;
    public static IntPtr ArrayGet(IntPtr array, nint index) => CFArrayGetValueAtIndex(array, index);
}
