using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MidiSharp.Hosting.Vst3.Vst3Abi;

namespace MidiSharp.Hosting.Vst3;

/// <summary>
/// A host-side <c>IEventList</c> handed to a VST3 instrument as <c>ProcessData.inputEvents</c>: the channel
/// that delivers note-on/note-off to the plugin. The host fills it before each <c>process</c> from the
/// block's queued MIDI; the plugin reads it back via <c>getEventCount</c>/<c>getEvent</c>. Backed by a
/// fixed-capacity unmanaged event buffer so the realtime fill and the plugin's reads allocate nothing.
/// The native object is a two-pointer block <c>[vtbl][GCHandle]</c>, like <see cref="Vst3BStream"/>.
/// </summary>
internal sealed unsafe class Vst3EventList : IDisposable
{
    private static readonly IntPtr SharedVtbl = BuildVtbl();

    private readonly VstEvent* _events;
    private readonly int _capacity;
    private int _count;
    private GCHandle _self;
    private void* _obj;
    private bool _disposed;

    public Vst3EventList(int capacity = 1024)
    {
        _capacity = capacity;
        _events = (VstEvent*)NativeMemory.AllocZeroed((nuint)capacity, (nuint)sizeof(VstEvent));
        _self = GCHandle.Alloc(this);
        _obj = NativeMemory.Alloc(2, (nuint)IntPtr.Size);
        ((IntPtr*)_obj)[0] = SharedVtbl;
        ((IntPtr*)_obj)[1] = GCHandle.ToIntPtr(_self);
    }

    /// <summary>The native <c>IEventList*</c> to set on <c>ProcessData.inputEvents</c>.</summary>
    public void* Pointer => _obj;

    public int Count => _count;

    /// <summary>Drop all events (call at the top of each block before re-filling).</summary>
    public void Clear() => _count = 0;

    public void AddNoteOn(int sampleOffset, int channel, int pitch, int velocity)
    {
        if (_count >= _capacity) return;
        ref var e = ref _events[_count++];
        e = default;
        e.Type = (ushort)kNoteOnEvent;
        e.SampleOffset = sampleOffset;
        e.OnChannel = (short)channel;
        e.OnPitch = (short)pitch;
        e.OnVelocity = velocity / 127f;
        e.OnNoteId = -1;
    }

    public void AddNoteOff(int sampleOffset, int channel, int pitch, int velocity)
    {
        if (_count >= _capacity) return;
        ref var e = ref _events[_count++];
        e = default;
        e.Type = (ushort)kNoteOffEvent;
        e.SampleOffset = sampleOffset;
        e.OffChannel = (short)channel;
        e.OffPitch = (short)pitch;
        e.OffVelocity = velocity / 127f;
        e.OffNoteId = -1;
    }

    private static Vst3EventList Self(void* self) => (Vst3EventList)GCHandle.FromIntPtr(((IntPtr*)self)[1]).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QueryInterface(void* self, byte* iid, void** obj)
    {
        if (IidEq(iid, IidEventList) || IidEq(iid, IidFUnknown)) { *obj = self; return ResultOk; }
        *obj = null; return -1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint RefOne(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetEventCount(void* self) => Self(self)._count;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetEvent(void* self, int index, VstEvent* e)
    {
        var s = Self(self);
        if ((uint)index >= (uint)s._count || e == null) return ResultFalse;
        *e = s._events[index];
        return ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AddEvent(void* self, VstEvent* e) => -1;   // kNotImplemented: host input list is read-only

    private static IntPtr BuildVtbl()
    {
        var v = (IntPtr*)NativeMemory.Alloc(6, (nuint)IntPtr.Size);
        v[0] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*, void**, int>)&QueryInterface;
        v[1] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        v[2] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        v[3] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, int>)&GetEventCount;
        v[4] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, int, VstEvent*, int>)&GetEvent;
        v[5] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, VstEvent*, int>)&AddEvent;
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
        if (_obj != null) { NativeMemory.Free(_obj); _obj = null; }
        if (_events != null) NativeMemory.Free(_events);
        if (_self.IsAllocated) _self.Free();
    }
}
