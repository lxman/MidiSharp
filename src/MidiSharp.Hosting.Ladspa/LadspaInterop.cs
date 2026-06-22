using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.Ladspa;

/// <summary>
/// The LADSPA 1.1 ABI, transcribed from <c>ladspa.h</c>. LADSPA is the reference simplicity case: a
/// shared object exports <c>ladspa_descriptor(index)</c> returning <see cref="LADSPA_Descriptor"/>
/// structs of function pointers. Effects only — no MIDI, no state, no GUI.
/// </summary>
/// <remarks>
/// Layout notes for LP64 Linux (the only platform LADSPA targets): C <c>unsigned long</c> is 8 bytes
/// (modelled as <see cref="nuint"/>); the <c>LADSPA_*Descriptor</c> typedefs over <c>int</c> stay 4
/// bytes; <c>LADSPA_Data</c> is <c>float</c>; <c>LADSPA_Handle</c> is <c>void*</c>.
/// </remarks>
internal static class LadspaInterop
{
    /// <summary>The single required export: <c>const LADSPA_Descriptor * ladspa_descriptor(unsigned long Index)</c>.</summary>
    public const string DescriptorExport = "ladspa_descriptor";

    // ── Port descriptor bit flags (LADSPA_PortDescriptor) ───────────────────────────────────────────
    public const int PortInput = 0x1;
    public const int PortOutput = 0x2;
    public const int PortControl = 0x4;
    public const int PortAudio = 0x8;

    public static bool IsInput(int pd) => (pd & PortInput) != 0;
    public static bool IsOutput(int pd) => (pd & PortOutput) != 0;
    public static bool IsControl(int pd) => (pd & PortControl) != 0;
    public static bool IsAudio(int pd) => (pd & PortAudio) != 0;

    // ── Port range hint bit flags (LADSPA_PortRangeHintDescriptor) ──────────────────────────────────
    public const int HintBoundedBelow = 0x1;
    public const int HintBoundedAbove = 0x2;
    public const int HintToggled = 0x4;
    public const int HintSampleRate = 0x8;     // bounds are multiples of the sample rate
    public const int HintLogarithmic = 0x10;
    public const int HintInteger = 0x20;

    // Default-value sub-field (LADSPA_HINT_DEFAULT_MASK = 0x3C0).
    public const int HintDefaultMask = 0x3C0;
    public const int HintDefaultNone = 0x0;
    public const int HintDefaultMinimum = 0x40;
    public const int HintDefaultLow = 0x80;
    public const int HintDefaultMiddle = 0xC0;
    public const int HintDefaultHigh = 0x100;
    public const int HintDefaultMaximum = 0x140;
    public const int HintDefault0 = 0x200;
    public const int HintDefault1 = 0x240;
    public const int HintDefault100 = 0x280;
    public const int HintDefault440 = 0x2C0;   // concert A

    [StructLayout(LayoutKind.Sequential)]
    public struct LADSPA_PortRangeHint
    {
        public int HintDescriptor;   // LADSPA_PortRangeHintDescriptor (int)
        public float LowerBound;     // LADSPA_Data
        public float UpperBound;     // LADSPA_Data
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LADSPA_Descriptor
    {
        public nuint UniqueID;            // unsigned long
        public IntPtr Label;              // const char *
        public int Properties;            // LADSPA_Properties (int)
        public IntPtr Name;               // const char *
        public IntPtr Maker;              // const char *
        public IntPtr Copyright;          // const char *
        public nuint PortCount;           // unsigned long
        public IntPtr PortDescriptors;    // const LADSPA_PortDescriptor *  (int[PortCount])
        public IntPtr PortNames;          // const char * const *           (char*[PortCount])
        public IntPtr PortRangeHints;     // const LADSPA_PortRangeHint *   (struct[PortCount])
        public IntPtr ImplementationData; // void *

        public IntPtr Instantiate;        // LADSPA_Handle (*)(const LADSPA_Descriptor*, unsigned long SampleRate)
        public IntPtr ConnectPort;        // void (*)(LADSPA_Handle, unsigned long Port, LADSPA_Data* DataLocation)
        public IntPtr Activate;           // void (*)(LADSPA_Handle)            — may be null
        public IntPtr Run;                // void (*)(LADSPA_Handle, unsigned long SampleCount)
        public IntPtr RunAdding;          // void (*)(LADSPA_Handle, unsigned long)  — may be null
        public IntPtr SetRunAddingGain;   // void (*)(LADSPA_Handle, LADSPA_Data)     — may be null
        public IntPtr Deactivate;         // void (*)(LADSPA_Handle)            — may be null
        public IntPtr Cleanup;            // void (*)(LADSPA_Handle)
    }

    // ── Function-pointer delegate signatures (bound via Marshal.GetDelegateForFunctionPointer) ──────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr DescriptorFn(nuint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr InstantiateFn(IntPtr descriptor, nuint sampleRate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void ConnectPortFn(IntPtr instance, nuint port, float* dataLocation);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ActivateFn(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void RunFn(IntPtr instance, nuint sampleCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DeactivateFn(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CleanupFn(IntPtr instance);

    /// <summary>Marshal a NUL-terminated C string pointer (UTF-8/ASCII) to a managed string, or "" if null.</summary>
    public static string Str(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";

    /// <summary>The plugin's default value for a port, resolved from its range hint (best-effort).</summary>
    public static float ResolveDefault(in LADSPA_PortRangeHint hint, int sampleRate)
    {
        int h = hint.HintDescriptor;
        float lower = hint.LowerBound * ((h & HintSampleRate) != 0 ? sampleRate : 1);
        float upper = hint.UpperBound * ((h & HintSampleRate) != 0 ? sampleRate : 1);
        return (h & HintDefaultMask) switch
        {
            HintDefaultMinimum => lower,
            HintDefaultLow => (h & HintLogarithmic) != 0 ? GeoMix(lower, upper, 0.75f) : Mix(lower, upper, 0.25f),
            HintDefaultMiddle => (h & HintLogarithmic) != 0 ? GeoMix(lower, upper, 0.5f) : Mix(lower, upper, 0.5f),
            HintDefaultHigh => (h & HintLogarithmic) != 0 ? GeoMix(lower, upper, 0.25f) : Mix(lower, upper, 0.75f),
            HintDefaultMaximum => upper,
            HintDefault0 => 0f,
            HintDefault1 => 1f,
            HintDefault100 => 100f,
            HintDefault440 => 440f,
            _ => (h & HintBoundedBelow) != 0 ? lower : (h & HintBoundedAbove) != 0 ? upper : 0f,
        };
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;

    private static float GeoMix(float a, float b, float t)
        => a > 0 && b > 0 ? (float)(Math.Exp(Math.Log(a) * (1 - t) + Math.Log(b) * t)) : Mix(a, b, t);
}
