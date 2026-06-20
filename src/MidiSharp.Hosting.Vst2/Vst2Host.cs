using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static MidiSharp.Hosting.Vst2.Vst2Abi;

namespace MidiSharp.Hosting.Vst2;

/// <summary>
/// The host side of VST2: a single <c>audioMaster</c> callback shared by every loaded plugin. Minimal —
/// it answers the basic queries a plugin needs at load/activate (VST version, sample rate, block size,
/// process level, vendor/product strings) and returns 0 for everything else. Host-global state
/// (sample rate / block size) is static because the one callback serves all plugins, which all run at
/// the host's single rate.
/// </summary>
internal static unsafe class Vst2Host
{
    public static int SampleRate = 48000;
    public static int BlockSize = 4096;

    private static readonly IntPtr Vendor = Ansi("MidiSharp");
    private static readonly IntPtr Product = Ansi("MidiSharp");

    /// <summary>The function pointer to hand to <c>VSTPluginMain</c>.</summary>
    public static IntPtr Callback => (IntPtr)(delegate* unmanaged[Cdecl]<AEffect*, int, int, IntPtr, void*, float, IntPtr>)&AudioMaster;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr AudioMaster(AEffect* effect, int opcode, int index, IntPtr value, void* ptr, float opt)
    {
        switch (opcode)
        {
            case AmVersion: return 2400;                       // VST 2.4
            case AmGetSampleRate: return SampleRate;
            case AmGetBlockSize: return BlockSize;
            case AmGetCurrentProcessLevel: return 2;           // realtime
            case AmGetVendorVersion: return 1;
            case AmGetVendorString: return WriteAnsi(ptr, "MidiSharp");
            case AmGetProductString: return WriteAnsi(ptr, "MidiSharp");
            // AmGetTime returns null (no transport info) — effects tolerate it; AmCanDo/Idle/Automate → 0.
            default: return IntPtr.Zero;
        }
    }

    private static IntPtr WriteAnsi(void* dst, string s)
    {
        if (dst == null) return IntPtr.Zero;
        var bytes = Encoding.ASCII.GetBytes(s);
        var p = (byte*)dst;
        var n = Math.Min(bytes.Length, 63);   // kVstMaxVendorStrLen is 64
        for (var i = 0; i < n; i++) p[i] = bytes[i];
        p[n] = 0;
        return 1;
    }

    private static IntPtr Ansi(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        var p = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        ((byte*)p)[bytes.Length] = 0;
        return p;
    }
}
