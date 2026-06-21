using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.Vst3.Vst3Abi;

namespace MidiSharp.Hosting.Vst3;

/// <summary>
/// The host frame a VST3 view is given via <c>setFrame</c>, plus the Linux <c>IRunLoop</c> the view queries
/// off it. A real editor registers its X11 connection fd (<c>registerEventHandler</c>) and animation timers
/// (<c>registerTimer</c>) with the run loop; we forward those to the editor thread's
/// <see cref="IEditorRunLoop"/>, which polls them and calls the plugin's handlers back on the UI thread.
/// </summary>
/// <remarks>
/// Two native COM objects (IPlugFrame, IRunLoop) share one managed instance via an embedded GCHandle, the
/// same pattern as the state/event-list bridges. The frame's <c>queryInterface</c> hands out the run loop.
/// </remarks>
internal sealed unsafe class Vst3PlugFrame : IDisposable
{
    private static readonly IntPtr FrameVtbl = BuildFrameVtbl();
    private static readonly IntPtr RunLoopVtbl = BuildRunLoopVtbl();

    private readonly IEditorRunLoop _loop;
    private readonly Dictionary<IntPtr, int> _handlerFds = [];   // IEventHandler* → fd, so unregister can find the fd
    private GCHandle _self;
    private void* _frame;
    private void* _runLoop;
    private bool _disposed;

    public Vst3PlugFrame(IEditorRunLoop loop)
    {
        _loop = loop;
        _self = GCHandle.Alloc(this);
        _frame = Make(FrameVtbl);
        _runLoop = Make(RunLoopVtbl);
    }

    /// <summary>The native <c>IPlugFrame*</c> to pass to <c>IPlugView.setFrame</c>.</summary>
    public void* Frame => _frame;

    private void* Make(IntPtr vtbl)
    {
        var obj = (IntPtr*)NativeMemory.Alloc(2, (nuint)IntPtr.Size);
        obj[0] = vtbl;
        obj[1] = GCHandle.ToIntPtr(_self);
        return obj;
    }

    private static Vst3PlugFrame Self(void* self) => (Vst3PlugFrame)GCHandle.FromIntPtr(((IntPtr*)self)[1]).Target!;

    // Invoke a native FUnknown-derived handler's first defined method (vtable slot 3): onFDIsSet(fd) / onTimer().
    private static void CallSlot3(void* handler, int fdOrNone)
    {
        var vtbl = *(IntPtr**)handler;
        if (fdOrNone >= 0) ((delegate* unmanaged[Cdecl]<void*, int, void>)vtbl[3])(handler, fdOrNone);
        else ((delegate* unmanaged[Cdecl]<void*, void>)vtbl[3])(handler);
    }

    // ── IPlugFrame ──
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FrameQueryInterface(void* self, byte* iid, void** obj)
    {
        var f = Self(self);
        if (IidEq(iid, IidPlugFrame) || IidEq(iid, IidFUnknown)) { *obj = self; return ResultOk; }
        // Steinberg::Linux::IRunLoop is X11-only; on Windows the editor drives itself via the Win32 message
        // pump, so we must not advertise a run loop there.
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) && IidEq(iid, IidRunLoop)) { *obj = f._runLoop; return ResultOk; }
        *obj = null; return NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint RefOne(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FrameResizeView(void* self, void* view, void* newSize) => ResultOk;

    // ── Linux::IRunLoop ──
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RunLoopQueryInterface(void* self, byte* iid, void** obj)
    {
        if (IidEq(iid, IidRunLoop) || IidEq(iid, IidFUnknown)) { *obj = self; return ResultOk; }
        *obj = null; return NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RegisterEventHandler(void* self, void* handler, int fd)
    {
        var f = Self(self);
        f._handlerFds[(IntPtr)handler] = fd;
        f._loop.RegisterFd(fd, () => CallSlot3(handler, fd));
        return ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int UnregisterEventHandler(void* self, void* handler)
    {
        var f = Self(self);
        if (f._handlerFds.Remove((IntPtr)handler, out var fd)) f._loop.UnregisterFd(fd);
        return ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RegisterTimer(void* self, void* handler, ulong ms)
    {
        Self(self)._loop.RegisterTimer((long)ms, (IntPtr)handler, () => CallSlot3(handler, -1));
        return ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int UnregisterTimer(void* self, void* handler)
    {
        Self(self)._loop.UnregisterTimer((IntPtr)handler);
        return ResultOk;
    }

    private static IntPtr BuildFrameVtbl()
    {
        var v = (IntPtr*)NativeMemory.Alloc(4, (nuint)IntPtr.Size);
        v[0] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*, void**, int>)&FrameQueryInterface;
        v[1] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        v[2] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        v[3] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void*, void*, int>)&FrameResizeView;
        return (IntPtr)v;
    }

    private static IntPtr BuildRunLoopVtbl()
    {
        var v = (IntPtr*)NativeMemory.Alloc(7, (nuint)IntPtr.Size);
        v[0] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*, void**, int>)&RunLoopQueryInterface;
        v[1] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        v[2] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        v[3] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void*, int, int>)&RegisterEventHandler;
        v[4] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void*, int>)&UnregisterEventHandler;
        v[5] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void*, ulong, int>)&RegisterTimer;
        v[6] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void*, int>)&UnregisterTimer;
        return (IntPtr)v;
    }

    private static bool IidEq(byte* a, byte[] b)
    {
        for (var i = 0; i < 16; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_frame != null) { NativeMemory.Free(_frame); _frame = null; }
        if (_runLoop != null) { NativeMemory.Free(_runLoop); _runLoop = null; }
        if (_self.IsAllocated) _self.Free();
    }
}
