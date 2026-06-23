using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.AudioUnit;

/// <summary>
/// Hand-built Objective-C <b>blocks</b> for the few AudioToolbox APIs that take a completion handler — AU v3's
/// async <c>AudioComponentInstantiate</c> (Plan A) and the editor's <c>requestViewControllerWithCompletionHandler:</c>
/// (Plan B). C# can't synthesize a real Obj-C block, so we lay the ABI struct out by hand: a non-capturing
/// <b>global</b> block <c>{ isa, flags, reserved, invoke, descriptor }</c> whose <c>invoke</c> points at an
/// <see cref="UnmanagedCallersOnlyAttribute"/> C function. The runtime calls it as <c>block-&gt;invoke(block, args…)</c>.
/// </summary>
/// <remarks>
/// Layout and field offsets were confirmed by the Plan A Task 0 spike (arm64): <c>isa = _NSConcreteGlobalBlock</c>
/// (the runtime's global-block class, resolved from libSystem), <c>flags = BLOCK_IS_GLOBAL (0x10000000)</c>,
/// <c>invoke</c> at offset 16, and a <c>Block_descriptor { reserved = 0; size = 32 }</c> at offset 24. A global
/// block owns no captured state, so it is never copied or freed — block and descriptor are process-lifetime
/// allocations (the few we make are one per long-lived completion site).
/// </remarks>
internal static unsafe class AuBlocks
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int BlockIsGlobal = 0x10000000;   // BLOCK_IS_GLOBAL — a compile-time-constant (global) block.

    /// <summary>The Obj-C runtime's class for global blocks; its address is the block's <c>isa</c>.</summary>
    private static readonly IntPtr NSConcreteGlobalBlock = ResolveExport(LibSystem, "_NSConcreteGlobalBlock");

    /// <summary>
    /// Build a non-capturing global block whose <c>invoke</c> is <paramref name="invoke"/> — a pointer to an
    /// <see cref="UnmanagedCallersOnlyAttribute"/> Cdecl function whose <i>first</i> parameter is the block itself
    /// (the Obj-C convention). The returned pointer is a stable global block usable directly as an AudioToolbox
    /// completion handler.
    /// </summary>
    public static IntPtr MakeGlobalBlock(IntPtr invoke)
    {
        // Block_descriptor_1: { unsigned long reserved; unsigned long size; } — size == the block literal's length.
        var descriptor = (byte*)NativeMemory.AllocZeroed(16);
        *(ulong*)(descriptor + 8) = 32;

        // Block_literal: { void* isa; int flags; int reserved; void* invoke; void* descriptor; } == 32 bytes.
        var block = (byte*)NativeMemory.AllocZeroed(32);
        *(IntPtr*)(block + 0) = NSConcreteGlobalBlock;
        *(int*)(block + 8) = BlockIsGlobal;
        *(IntPtr*)(block + 16) = invoke;
        *(IntPtr*)(block + 24) = (IntPtr)descriptor;
        return (IntPtr)block;
    }

    /// <summary>Resolve a data export (e.g. <c>_NSConcreteGlobalBlock</c>), tolerating the leading-underscore
    /// mangling differences between the symbol table and the C name.</summary>
    private static IntPtr ResolveExport(string library, string symbol)
    {
        IntPtr handle = NativeLibrary.Load(library);
        foreach (string name in new[] { symbol, "_" + symbol, symbol.TrimStart('_') })
            if (NativeLibrary.TryGetExport(handle, name, out IntPtr p)) return p;
        throw new InvalidOperationException($"Symbol '{symbol}' not found in {library}.");
    }
}
