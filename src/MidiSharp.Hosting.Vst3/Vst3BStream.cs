using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MidiSharp.Hosting.Vst3.Vst3Abi;

namespace MidiSharp.Hosting.Vst3;

/// <summary>
/// A host-side <c>IBStream</c> over a managed byte buffer — the conduit for VST3 component/controller
/// state. <see cref="ForWrite"/> gives a plugin a growable sink for <c>getState</c>; <see cref="ForRead"/>
/// replays saved bytes into <c>setState</c>/<c>setComponentState</c>. The native object is a two-pointer
/// block <c>[vtbl][GCHandle]</c>; the shared vtable's <see cref="UnmanagedCallersOnly"/> methods recover
/// the managed instance from the embedded handle. State is exchanged on the control thread (load/save),
/// never the audio thread, so the simple growable buffer is fine.
/// </summary>
internal sealed unsafe class Vst3BStream : IDisposable
{
    private static readonly IntPtr SharedVtbl = BuildVtbl();

    private byte[] _data;
    private int _length;
    private int _pos;
    private GCHandle _self;
    private void* _obj;
    private bool _disposed;

    private Vst3BStream(byte[] data, int length)
    {
        _data = data;
        _length = length;
        _self = GCHandle.Alloc(this);
        _obj = NativeMemory.Alloc(2, (nuint)IntPtr.Size);
        ((IntPtr*)_obj)[0] = SharedVtbl;
        ((IntPtr*)_obj)[1] = GCHandle.ToIntPtr(_self);
    }

    public static Vst3BStream ForWrite() => new(new byte[256], 0);
    public static Vst3BStream ForRead(ReadOnlySpan<byte> data) => new(data.ToArray(), data.Length);

    /// <summary>The native <c>IBStream*</c> to hand to a plugin.</summary>
    public void* Pointer => _obj;

    /// <summary>The bytes written so far (after a <c>getState</c>), copied out.</summary>
    public byte[] ToArray() => _data.AsSpan(0, _length).ToArray();

    /// <summary>Rewind to the start so a freshly-written stream can be replayed into another setState call.</summary>
    public void Rewind() => _pos = 0;

    private static Vst3BStream Self(void* self) => (Vst3BStream)GCHandle.FromIntPtr(((IntPtr*)self)[1]).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QueryInterface(void* self, byte* iid, void** obj)
    {
        if (IidEq(iid, IidBStream) || IidEq(iid, IidFUnknown)) { *obj = self; return ResultOk; }
        *obj = null; return -1;   // kNoInterface
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint RefOne(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(void* self, void* buffer, int numBytes, int* numRead)
    {
        var s = Self(self);
        var n = Math.Max(0, Math.Min(numBytes, s._length - s._pos));
        if (n > 0) new ReadOnlySpan<byte>(s._data, s._pos, n).CopyTo(new Span<byte>(buffer, n));
        s._pos += n;
        if (numRead != null) *numRead = n;
        return ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Write(void* self, void* buffer, int numBytes, int* numWritten)
    {
        var s = Self(self);
        if (numBytes < 0) numBytes = 0;
        var end = s._pos + numBytes;
        if (end > s._data.Length)
        {
            var cap = s._data.Length == 0 ? 256 : s._data.Length;
            while (cap < end) cap *= 2;
            Array.Resize(ref s._data, cap);
        }
        if (numBytes > 0) new ReadOnlySpan<byte>(buffer, numBytes).CopyTo(s._data.AsSpan(s._pos, numBytes));
        s._pos = end;
        if (end > s._length) s._length = end;
        if (numWritten != null) *numWritten = numBytes;
        return ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Seek(void* self, long pos, int mode, long* result)
    {
        var s = Self(self);
        var target = mode switch { 1 => s._pos + pos, 2 => s._length + pos, _ => pos };   // cur / end / set
        if (target < 0) target = 0;
        s._pos = (int)target;
        if (result != null) *result = s._pos;
        return ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Tell(void* self, long* pos)
    {
        if (pos != null) *pos = Self(self)._pos;
        return ResultOk;
    }

    private static IntPtr BuildVtbl()
    {
        var v = (IntPtr*)NativeMemory.Alloc(7, (nuint)IntPtr.Size);
        v[0] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*, void**, int>)&QueryInterface;
        v[1] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        v[2] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, uint>)&RefOne;
        v[3] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void*, int, int*, int>)&Read;
        v[4] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void*, int, int*, int>)&Write;
        v[5] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, long, int, long*, int>)&Seek;
        v[6] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, long*, int>)&Tell;
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
        if (_self.IsAllocated) _self.Free();
    }
}
