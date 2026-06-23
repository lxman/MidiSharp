using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using static MidiSharp.Hosting.AudioUnit.AudioUnitAbi;

namespace MidiSharp.Hosting.AudioUnit;

/// <summary>
/// The Objective-C slice for hosting an AU through the modern <c>AUAudioUnit</c> front-end (AU v3): async
/// instantiation, bus-format negotiation via <c>AVAudioFormat</c>, render-resource lifecycle, and the realtime
/// <c>renderBlock</c>. Peer to <see cref="AuAppKit"/> (which it reuses for the editor's NSView ops). arm64-only;
/// <c>objc_msgSend</c> is uniform there. All proven by the Plan B Task 0 spikes (editor + render).
/// </summary>
/// <remarks>
/// v3 audio can't reuse the v2 C API: an out-of-process AU's bridged v2 instance is unavailable from the
/// <c>AUAudioUnit</c>, so audio/params/state/MIDI and the custom view all ride the one <c>AUAudioUnit</c> object.
/// </remarks>
internal static unsafe class AuV3
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string AVFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    [DllImport(Objc, CharSet = CharSet.Ansi)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Objc, CharSet = CharSet.Ansi)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendIdx(IntPtr self, IntPtr sel, nint i);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendFmt(IntPtr self, IntPtr sel, double sr, uint ch);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void SendSetU32(IntPtr self, IntPtr sel, uint v);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern byte SendFormatErr(IntPtr self, IntPtr sel, IntPtr fmt, IntPtr* err);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern byte SendErr(IntPtr self, IntPtr sel, IntPtr* err);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void Instantiate(IntPtr cls, IntPtr sel, AudioComponentDescription desc, uint options, IntPtr block);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern float SendFloat(IntPtr self, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void SendSetFloat(IntPtr self, IntPtr sel, float v);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern ulong SendU64(IntPtr self, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern uint SendU32(IntPtr self, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nuint SendNUInt(IntPtr self, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void SendSet(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern byte SendBool(IntPtr self, IntPtr sel);

    private static readonly IntPtr SelAlloc = sel_registerName("alloc");
    private static readonly IntPtr SelInputBusses = sel_registerName("inputBusses");
    private static readonly IntPtr SelOutputBusses = sel_registerName("outputBusses");
    private static readonly IntPtr SelObjectAtIndex = sel_registerName("objectAtIndexedSubscript:");
    private static readonly IntPtr SelSetFormatError = sel_registerName("setFormat:error:");
    private static readonly IntPtr SelSetMaxFrames = sel_registerName("setMaximumFramesToRender:");
    private static readonly IntPtr SelAllocateRR = sel_registerName("allocateRenderResourcesAndReturnError:");
    private static readonly IntPtr SelDeallocateRR = sel_registerName("deallocateRenderResources");
    private static readonly IntPtr SelRenderBlock = sel_registerName("renderBlock");
    private static readonly IntPtr SelStdFormat = sel_registerName("initStandardFormatWithSampleRate:channels:");
    private static readonly IntPtr SelParameterTree = sel_registerName("parameterTree");
    private static readonly IntPtr SelAllParameters = sel_registerName("allParameters");
    private static readonly IntPtr SelCount = sel_registerName("count");
    private static readonly IntPtr SelObjectAt = sel_registerName("objectAtIndex:");
    private static readonly IntPtr SelAddress = sel_registerName("address");
    private static readonly IntPtr SelMinValue = sel_registerName("minValue");
    private static readonly IntPtr SelMaxValue = sel_registerName("maxValue");
    private static readonly IntPtr SelValue = sel_registerName("value");
    private static readonly IntPtr SelSetValue = sel_registerName("setValue:");
    private static readonly IntPtr SelUnit = sel_registerName("unit");
    private static readonly IntPtr SelDisplayName = sel_registerName("displayName");
    private static readonly IntPtr SelFullState = sel_registerName("fullState");
    private static readonly IntPtr SelSetFullState = sel_registerName("setFullState:");
    private static readonly IntPtr SelScheduleMidi = sel_registerName("scheduleMIDIEventBlock");
    private static readonly IntPtr SelProvidesUI = sel_registerName("providesUserInterface");
    private static readonly IntPtr SelRequestVC = sel_registerName("requestViewControllerWithCompletionHandler:");
    private static readonly IntPtr SelView = sel_registerName("view");

    static AuV3() => NativeLibrary.TryLoad(AVFoundation, out _);   // registers AVAudioFormat

    // ── async instantiation: AUAudioUnit.instantiateWithComponentDescription:options:completionHandler: ──
    // The completion lands on the main dispatch queue (pump, don't block — same as Plan A). One process-global
    // block + a gate serialize the rare loads.
    private static readonly object s_gate = new();
    private static IntPtr s_au; private static volatile bool s_done;
    private static readonly IntPtr s_completionBlock =
        AuBlocks.MakeGlobalBlock((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnInstantiated);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnInstantiated(IntPtr block, IntPtr au, IntPtr error)
    {
        if (au != IntPtr.Zero) AuAppKit.Retain(au);   // client must retain the delivered AUAudioUnit
        s_au = au;
        s_done = true;
    }

    /// <summary>Async-instantiate an <c>AUAudioUnit</c> for the component, pumping the run loop until the
    /// main-queue completion fires. Returns the retained object (caller releases on dispose).</summary>
    public static IntPtr Instantiate(uint type, uint sub, uint manu, uint options, string id)
    {
        lock (s_gate)
        {
            s_au = IntPtr.Zero;
            s_done = false;
            var desc = new AudioComponentDescription { ComponentType = type, ComponentSubType = sub, ComponentManufacturer = manu };
            IntPtr cls = objc_getClass("AUAudioUnit");
            if (cls == IntPtr.Zero) throw new InvalidOperationException("AUAudioUnit class not available.");
            Instantiate(cls, sel_registerName("instantiateWithComponentDescription:options:completionHandler:"), desc, options, s_completionBlock);
            for (int i = 0; i < 400 && !s_done; i++) CoreFoundation.PumpRunLoop(0.05);
            if (!s_done) throw new InvalidOperationException($"AUAudioUnit instantiation of '{id}' timed out.");
            if (s_au == IntPtr.Zero) throw new InvalidOperationException($"AUAudioUnit instantiation of '{id}' returned nil.");
            return s_au;
        }
    }

    /// <summary>A standard <c>AVAudioFormat</c> — deinterleaved float32, matching the engine's planar bus.</summary>
    public static IntPtr StandardFormat(int sampleRate, int channels)
    {
        IntPtr cls = objc_getClass("AVAudioFormat");
        return cls == IntPtr.Zero ? IntPtr.Zero
             : SendFmt(Send(cls, SelAlloc), SelStdFormat, sampleRate, (uint)channels);
    }

    /// <summary>Set bus 0's format on the input or output bus array. False if the bus rejects it.</summary>
    public static bool SetBusFormat(IntPtr au, bool input, IntPtr format)
    {
        IntPtr busArray = Send(au, input ? SelInputBusses : SelOutputBusses);
        if (busArray == IntPtr.Zero) return false;
        IntPtr bus0 = SendIdx(busArray, SelObjectAtIndex, 0);
        if (bus0 == IntPtr.Zero) return input;   // an instrument may have no input bus — not an error
        IntPtr err = IntPtr.Zero;
        return SendFormatErr(bus0, SelSetFormatError, format, &err) != 0;
    }

    public static void SetMaxFrames(IntPtr au, int frames) => SendSetU32(au, SelSetMaxFrames, (uint)frames);

    public static bool AllocateRenderResources(IntPtr au)
    {
        IntPtr err = IntPtr.Zero;
        return SendErr(au, SelAllocateRR, &err) != 0;
    }

    public static void DeallocateRenderResources(IntPtr au) => Send(au, SelDeallocateRR);

    /// <summary>The cached realtime <c>renderBlock</c> (retained). Call its invoke as <c>block(block, …)</c>.</summary>
    public static IntPtr RenderBlock(IntPtr au)
    {
        IntPtr block = Send(au, SelRenderBlock);
        return block == IntPtr.Zero ? IntPtr.Zero : AuAppKit.Retain(block);
    }

    /// <summary>An Obj-C block's <c>invoke</c> function pointer (offset 16 in the block literal).</summary>
    public static IntPtr InvokeOf(IntPtr block) => *(IntPtr*)(block + 16);

    // ── parameters (AUParameterTree.allParameters → AUParameter objects; values are raw, in [min,max]) ──
    public static IntPtr ParameterTree(IntPtr au) => Send(au, SelParameterTree);
    public static IntPtr AllParameters(IntPtr tree) => tree == IntPtr.Zero ? IntPtr.Zero : Send(tree, SelAllParameters);
    public static int ArrayCount(IntPtr arr) => arr == IntPtr.Zero ? 0 : (int)SendNUInt(arr, SelCount);
    public static IntPtr ArrayAt(IntPtr arr, int i) => SendIdx(arr, SelObjectAt, i);

    public static ulong ParamAddress(IntPtr p) => SendU64(p, SelAddress);
    public static float ParamMin(IntPtr p) => SendFloat(p, SelMinValue);
    public static float ParamMax(IntPtr p) => SendFloat(p, SelMaxValue);
    public static float ParamValue(IntPtr p) => SendFloat(p, SelValue);
    public static void SetParamValue(IntPtr p, float v) => SendSetFloat(p, SelSetValue, v);
    public static uint ParamUnit(IntPtr p) => SendU32(p, SelUnit);
    public static string ParamDisplayName(IntPtr p) => CoreFoundation.ToManaged(Send(p, SelDisplayName));

    // ── state (AUAudioUnit.fullState — an NSDictionary of property-list objects, toll-free CFDictionary) ──
    public static IntPtr FullState(IntPtr au) => Send(au, SelFullState);
    public static void SetFullState(IntPtr au, IntPtr dict) => SendSet(au, SelSetFullState, dict);

    /// <summary>The cached <c>scheduleMIDIEventBlock</c> (retained) for instruments; nil for effects. Invoke as
    /// <c>block(block, eventSampleTime, cable, length, midiBytes)</c> with eventSampleTime
    /// <see cref="MidiEventTimeImmediate"/> + buffer offset.</summary>
    public static IntPtr ScheduleMidiBlock(IntPtr au)
    {
        IntPtr block = Send(au, SelScheduleMidi);
        return block == IntPtr.Zero ? IntPtr.Zero : AuAppKit.Retain(block);
    }

    /// <summary><c>AUEventSampleTimeImmediate</c> — schedule at "immediate + buffer offset" within the render.</summary>
    public const long MidiEventTimeImmediate = unchecked((long)0xffffffff00000000UL);

    // ── editor (the AU's custom view controller) ──
    /// <summary>True when the AU vends a custom view controller (so <c>requestViewController…</c> yields a view).</summary>
    public static bool ProvidesUserInterface(IntPtr au) => SendBool(au, SelProvidesUI) != 0;

    /// <summary>An NSViewController's <c>view</c> (an NSView).</summary>
    public static IntPtr ViewControllerView(IntPtr vc) => Send(vc, SelView);

    // requestViewControllerWithCompletionHandler: delivers on the main queue (pump, don't block — like instantiate).
    private static readonly object s_vcGate = new();
    private static IntPtr s_vc; private static volatile bool s_vcDone;
    private static readonly IntPtr s_vcBlock =
        AuBlocks.MakeGlobalBlock((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnViewController);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnViewController(IntPtr block, IntPtr vc)
    {
        if (vc != IntPtr.Zero) AuAppKit.Retain(vc);
        s_vc = vc;
        s_vcDone = true;
    }

    /// <summary>Request the AU's view controller (main-thread; pumps until the main-queue completion fires).
    /// Returns the retained controller, or <see cref="IntPtr.Zero"/> when the AU vends none.</summary>
    public static IntPtr RequestViewController(IntPtr au)
    {
        lock (s_vcGate)
        {
            s_vc = IntPtr.Zero;
            s_vcDone = false;
            SendSet(au, SelRequestVC, s_vcBlock);
            for (int i = 0; i < 200 && !s_vcDone; i++) CoreFoundation.PumpRunLoop(0.05);
            return s_vc;
        }
    }

    public static void Release(IntPtr obj) => AuAppKit.Release(obj);
}
