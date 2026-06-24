using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static MidiSharp.Hosting.Vst3.Vst3Abi;

namespace MidiSharp.Hosting.Vst3;

/// <summary>
/// Host-provided VST3 <c>IMessage</c> + <c>IAttributeList</c>, manufactured on demand by <see cref="Vst3Host"/>'s
/// <c>IHostApplication::createInstance</c>. These are the component↔controller messaging objects: a plugin asks
/// the host to allocate a message, fills its attribute list, and sends it across the connection. A host that
/// can't create them breaks any plugin that relies on messaging — its controller never receives the state its
/// editor needs and <c>createView</c> faults. Carla and Ardour both implement exactly this; we previously
/// returned kNoInterface for everything.
/// </summary>
/// <remarks>
/// Unlike the singleton callbacks in <see cref="Vst3Host"/>, each message is per-call and reference-counted:
/// the plugin frees it via <c>release()</c>. The native interface block is <c>[vtable][GCHandle]</c>; the
/// handle points at a managed <see cref="State"/> (the attribute store + bookkeeping) freed when the last
/// reference goes. The message owns its one attribute list (VST3: one list per message) and frees both
/// together — so the attribute list's own addRef/release are no-ops.
/// </remarks>
internal static unsafe class Vst3HostMessage
{
    private static readonly IntPtr MsgVtbl = BuildMessageVtbl();
    private static readonly IntPtr AttrVtbl = BuildAttributeVtbl();

    private readonly struct Blob(IntPtr ptr, uint size)
    {
        public readonly IntPtr Ptr = ptr;
        public readonly uint Size = size;
    }

    private sealed class State
    {
        public readonly Dictionary<string, object> Attributes = new();   // boxed long | double | string | Blob
        public int RefCount = 1;
        public GCHandle Handle;
        public void* MessageObj;
        public void* AttributeObj;
        public byte* MessageId;   // native ASCII (FIDString) returned by getMessageID; stable until release
    }

    /// <summary>Create a new <c>IMessage</c> and return its interface pointer; the caller owns one reference.</summary>
    public static void* Create()
    {
        var state = new State { Handle = default };
        state.Handle = GCHandle.Alloc(state);
        IntPtr h = GCHandle.ToIntPtr(state.Handle);
        state.MessageObj = AllocObject(MsgVtbl, h);
        state.AttributeObj = AllocObject(AttrVtbl, h);
        return state.MessageObj;
    }

    // A COM object is [vtable-pointer][GCHandle-to-State]; the static callbacks recover the State from slot 1.
    private static void* AllocObject(IntPtr vtbl, IntPtr handle)
    {
        var obj = (IntPtr*)NativeMemory.Alloc(2, (nuint)IntPtr.Size);
        obj[0] = vtbl;
        obj[1] = handle;
        return obj;
    }

    private static State Get(void* self) => (State)GCHandle.FromIntPtr(((IntPtr*)self)[1]).Target!;

    private static string Key(byte* id) => id == null ? "" : Marshal.PtrToStringAnsi((IntPtr)id) ?? "";

    private static bool IidEq(byte* a, byte[] b)
    {
        for (var i = 0; i < 16; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void Put(State s, byte* id, object value)
    {
        string key = Key(id);
        if (s.Attributes.TryGetValue(key, out object? old) && old is Blob ob) NativeMemory.Free((void*)ob.Ptr);
        s.Attributes[key] = value;
    }

    private static void FreeState(State s)
    {
        if (s.MessageId != null) { NativeMemory.Free(s.MessageId); s.MessageId = null; }
        foreach (object v in s.Attributes.Values) if (v is Blob b) NativeMemory.Free((void*)b.Ptr);
        s.Attributes.Clear();
        if (s.MessageObj != null) { NativeMemory.Free(s.MessageObj); s.MessageObj = null; }
        if (s.AttributeObj != null) { NativeMemory.Free(s.AttributeObj); s.AttributeObj = null; }
        if (s.Handle.IsAllocated) s.Handle.Free();
    }

    // ── IMessage ──────────────────────────────────────────────────────────────────────────────────────────
    private static IntPtr BuildMessageVtbl()
    {
        var v = (MessageVtbl*)NativeMemory.Alloc((nuint)sizeof(MessageVtbl));
        v->QueryInterface = &MsgQueryInterface;
        v->AddRef = &MsgAddRef;
        v->Release = &MsgRelease;
        v->GetMessageId = &MsgGetMessageId;
        v->SetMessageId = &MsgSetMessageId;
        v->GetAttributes = &MsgGetAttributes;
        return (IntPtr)v;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int MsgQueryInterface(void* self, byte* iid, void** obj)
    {
        if (IidEq(iid, IidMessage) || IidEq(iid, IidFUnknown))
        {
            State s = Get(self);
            Interlocked.Increment(ref s.RefCount);
            *obj = self;
            return ResultOk;
        }
        *obj = null;
        return NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint MsgAddRef(void* self) => (uint)Interlocked.Increment(ref Get(self).RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint MsgRelease(void* self)
    {
        State s = Get(self);
        int c = Interlocked.Decrement(ref s.RefCount);
        if (c == 0) FreeState(s);
        return (uint)c;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* MsgGetMessageId(void* self) => Get(self).MessageId;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MsgSetMessageId(void* self, byte* id)
    {
        State s = Get(self);
        if (s.MessageId != null) { NativeMemory.Free(s.MessageId); s.MessageId = null; }
        if (id == null) return;
        nuint len = 0; while (id[len] != 0) len++;
        var buf = (byte*)NativeMemory.Alloc(len + 1);
        Buffer.MemoryCopy(id, buf, len + 1, len + 1);   // copy including the NUL terminator
        s.MessageId = buf;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* MsgGetAttributes(void* self) => Get(self).AttributeObj;

    // ── IAttributeList ────────────────────────────────────────────────────────────────────────────────────
    private static IntPtr BuildAttributeVtbl()
    {
        var v = (AttributeListVtbl*)NativeMemory.Alloc((nuint)sizeof(AttributeListVtbl));
        v->QueryInterface = &AttrQueryInterface;
        v->AddRef = &AttrAddRef;
        v->Release = &AttrRelease;
        v->SetInt = &AttrSetInt;
        v->GetInt = &AttrGetInt;
        v->SetFloat = &AttrSetFloat;
        v->GetFloat = &AttrGetFloat;
        v->SetString = &AttrSetString;
        v->GetString = &AttrGetString;
        v->SetBinary = &AttrSetBinary;
        v->GetBinary = &AttrGetBinary;
        return (IntPtr)v;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AttrQueryInterface(void* self, byte* iid, void** obj)
    {
        if (IidEq(iid, IidAttributeList) || IidEq(iid, IidFUnknown)) { *obj = self; return ResultOk; }
        *obj = null;
        return NoInterface;
    }

    // The attribute list lives and dies with its message, so its own refcount is a no-op (returns 1).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint AttrAddRef(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint AttrRelease(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AttrSetInt(void* self, byte* id, long value) { Put(Get(self), id, value); return ResultOk; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AttrGetInt(void* self, byte* id, long* value)
    {
        if (Get(self).Attributes.TryGetValue(Key(id), out object? v) && v is long l) { *value = l; return ResultOk; }
        return ResultFalse;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AttrSetFloat(void* self, byte* id, double value) { Put(Get(self), id, value); return ResultOk; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AttrGetFloat(void* self, byte* id, double* value)
    {
        if (Get(self).Attributes.TryGetValue(Key(id), out object? v) && v is double d) { *value = d; return ResultOk; }
        return ResultFalse;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AttrSetString(void* self, byte* id, char* value)
    {
        Put(Get(self), id, value == null ? "" : new string(value));
        return ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AttrGetString(void* self, byte* id, char* value, uint size)
    {
        if (size > 0 && Get(self).Attributes.TryGetValue(Key(id), out object? v) && v is string s)
        {
            int n = Math.Min(s.Length, (int)size - 1);
            for (var i = 0; i < n; i++) value[i] = s[i];
            value[n] = '\0';
            return ResultOk;
        }
        return ResultFalse;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AttrSetBinary(void* self, byte* id, void* data, uint size)
    {
        void* buf = NativeMemory.Alloc(size == 0 ? 1 : size);
        if (size > 0) Buffer.MemoryCopy(data, buf, size, size);
        Put(Get(self), id, new Blob((IntPtr)buf, size));
        return ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AttrGetBinary(void* self, byte* id, void** data, uint* size)
    {
        if (Get(self).Attributes.TryGetValue(Key(id), out object? v) && v is Blob b)
        {
            *data = (void*)b.Ptr;
            *size = b.Size;
            return ResultOk;
        }
        *data = null; *size = 0;
        return ResultFalse;
    }
}
