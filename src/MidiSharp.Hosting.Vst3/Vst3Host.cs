using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MidiSharp.Hosting.Vst3.Vst3Abi;

namespace MidiSharp.Hosting.Vst3;

/// <summary>
/// The host side of VST3: minimal <c>IHostApplication</c> and <c>IComponentHandler</c> objects handed to
/// a plugin at <c>initialize</c> / <c>setComponentHandler</c>. Each is a COM object — a pointer to a
/// <c>{ lpVtbl }</c> struct whose vtable holds function pointers to <see cref="UnmanagedCallersOnly"/>
/// methods (the interface pointer is the explicit first argument). One shared instance of each lives for
/// the process; they are not ref-counted (addRef/release return 1).
/// </summary>
internal static unsafe class Vst3Host
{
    private static readonly IntPtr HostApp = BuildHostApplication();
    private static readonly IntPtr Handler = BuildComponentHandler();
    private static readonly IntPtr Frame = BuildPlugFrame();

    public static void* HostApplication => (void*)HostApp;
    public static void* ComponentHandler => (void*)Handler;
    /// <summary>A minimal <c>IPlugFrame</c> for a plugin view's <c>setFrame</c> (resizeView is accepted).</summary>
    public static void* PlugFrame => (void*)Frame;

    private static bool IidEq(byte* a, byte[] b)
    {
        for (var i = 0; i < 16; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ── IHostApplication ──
    private static IntPtr BuildHostApplication()
    {
        var vtbl = (IntPtr*)NativeMemory.Alloc(5, (nuint)IntPtr.Size);
        vtbl[0] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*, void**, int>)&HostQueryInterface;
        vtbl[1] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        vtbl[2] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        vtbl[3] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, char*, int>)&HostGetName;
        vtbl[4] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*, byte*, void**, int>)&HostCreateInstance;
        var obj = (IntPtr*)NativeMemory.Alloc((nuint)IntPtr.Size);
        obj[0] = (IntPtr)vtbl;
        return (IntPtr)obj;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HostQueryInterface(void* self, byte* iid, void** obj)
    {
        if (IidEq(iid, IidHostApplication) || IidEq(iid, IidFUnknown)) { *obj = self; return ResultOk; }
        *obj = null; return NoInterface;   // kNoInterface
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint RefOne(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HostGetName(void* self, char* name)
    {
        const string n = "MidiSharp";
        for (var i = 0; i < n.Length; i++) name[i] = n[i];
        name[n.Length] = '\0';
        return ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HostCreateInstance(void* self, byte* cid, byte* iid, void** obj) { *obj = null; return NoInterface; }

    // ── IComponentHandler ──
    private static IntPtr BuildComponentHandler()
    {
        var vtbl = (IntPtr*)NativeMemory.Alloc(7, (nuint)IntPtr.Size);
        vtbl[0] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*, void**, int>)&HandlerQueryInterface;
        vtbl[1] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        vtbl[2] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        vtbl[3] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint, int>)&HandlerEdit;       // beginEdit
        vtbl[4] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint, double, int>)&HandlerPerformEdit;
        vtbl[5] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint, int>)&HandlerEdit;        // endEdit
        vtbl[6] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, int, int>)&HandlerRestart;
        var obj = (IntPtr*)NativeMemory.Alloc((nuint)IntPtr.Size);
        obj[0] = (IntPtr)vtbl;
        return (IntPtr)obj;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HandlerQueryInterface(void* self, byte* iid, void** obj)
    {
        if (IidEq(iid, IidComponentHandler) || IidEq(iid, IidFUnknown)) { *obj = self; return ResultOk; }
        *obj = null; return NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HandlerEdit(void* self, uint id) => ResultOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HandlerPerformEdit(void* self, uint id, double value) => ResultOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HandlerRestart(void* self, int flags) => ResultOk;

    // ── IPlugFrame ── (a plugin view calls resizeView on it; we accept and let the host window follow)
    private static IntPtr BuildPlugFrame()
    {
        var vtbl = (IntPtr*)NativeMemory.Alloc(4, (nuint)IntPtr.Size);
        vtbl[0] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*, void**, int>)&FrameQueryInterface;
        vtbl[1] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        vtbl[2] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        vtbl[3] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void*, void*, int>)&FrameResizeView;
        var obj = (IntPtr*)NativeMemory.Alloc((nuint)IntPtr.Size);
        obj[0] = (IntPtr)vtbl;
        return (IntPtr)obj;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FrameQueryInterface(void* self, byte* iid, void** obj)
    {
        if (IidEq(iid, IidPlugFrame) || IidEq(iid, IidFUnknown)) { *obj = self; return ResultOk; }
        *obj = null; return NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FrameResizeView(void* self, void* view, void* newSize) => ResultOk;
}
