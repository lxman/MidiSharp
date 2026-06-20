using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.Vst3;

/// <summary>
/// The VST3 ABI transcribed clean-room from the public vst3_c_api.h. Each interface is a pointer to a
/// struct whose first field (<c>lpVtbl</c>) points to a vtable of function pointers; every method takes
/// the interface pointer as an explicit first argument (the C API makes <c>this</c> explicit, so calls
/// are plain cdecl). Vtable structs list methods in exact ABI order; ones the host never calls are kept
/// as <see cref="IntPtr"/> so offsets stay exact. IIDs use the non-Windows (Linux/macOS) UID byte order.
/// </summary>
internal static unsafe class Vst3Abi
{
    public const int ResultOk = 0;          // kResultOk == kResultTrue == 0
    public const int ResultFalse = 1;
    public static bool Ok(int r) => r == ResultOk;

    public static readonly string[] FactoryExport = ["GetPluginFactory"];
    public const string AudioModuleCategory = "Audio Module Class";

    // ── IIDs (non-Windows UID layout: each of the four uint32 stored big-endian) ──
    public static readonly byte[] IidFUnknown = Uid(0x00000000, 0x00000000, 0xC0000000, 0x00000046);
    public static readonly byte[] IidPluginFactory = Uid(0x7A4D811C, 0x52114A1F, 0xAED9D2EE, 0x0B43BF9F);
    public static readonly byte[] IidComponent = Uid(0xE831FF31, 0xF2D54301, 0x928EBBEE, 0x25697802);
    public static readonly byte[] IidAudioProcessor = Uid(0x42043F99, 0xB7DA453C, 0xA569E79D, 0x9AAEC33D);
    public static readonly byte[] IidEditController = Uid(0xDCD7BBE3, 0x7742448D, 0xA874AACC, 0x979C759E);
    public static readonly byte[] IidHostApplication = Uid(0x58E595CC, 0xDB2D4969, 0x8B6AAF8C, 0x36A664E5);
    public static readonly byte[] IidComponentHandler = Uid(0x93A0BEA3, 0x0BD045DB, 0x8E890B0C, 0xC1E46AC6);

    public static byte[] Uid(uint a, uint b, uint c, uint d) =>
    [
        (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a,
        (byte)(b >> 24), (byte)(b >> 16), (byte)(b >> 8), (byte)b,
        (byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c,
        (byte)(d >> 24), (byte)(d >> 16), (byte)(d >> 8), (byte)d,
    ];

    // ── Vtable structs (method order is the ABI; only called methods are typed) ──
    [StructLayout(LayoutKind.Sequential)]
    public struct FactoryVtbl
    {
        public IntPtr QueryInterface, AddRef, Release, GetFactoryInfo;
        public delegate* unmanaged[Cdecl]<void*, int> CountClasses;
        public delegate* unmanaged[Cdecl]<void*, int, PClassInfo*, int> GetClassInfo;
        public delegate* unmanaged[Cdecl]<void*, byte*, byte*, void**, int> CreateInstance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComponentVtbl
    {
        public delegate* unmanaged[Cdecl]<void*, byte*, void**, int> QueryInterface;
        public IntPtr AddRef;
        public delegate* unmanaged[Cdecl]<void*, uint> Release;
        public delegate* unmanaged[Cdecl]<void*, void*, int> Initialize;
        public delegate* unmanaged[Cdecl]<void*, int> Terminate;
        public IntPtr GetControllerClassId, SetIoMode;
        public delegate* unmanaged[Cdecl]<void*, int, int, int> GetBusCount;
        public delegate* unmanaged[Cdecl]<void*, int, int, int, BusInfo*, int> GetBusInfo;
        public IntPtr GetRoutingInfo;
        public delegate* unmanaged[Cdecl]<void*, int, int, int, byte, int> ActivateBus;
        public delegate* unmanaged[Cdecl]<void*, byte, int> SetActive;
        public IntPtr SetState, GetState;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioProcessorVtbl
    {
        public IntPtr QueryInterface, AddRef, Release, SetBusArrangements, GetBusArrangement;
        public delegate* unmanaged[Cdecl]<void*, int, int> CanProcessSampleSize;
        public IntPtr GetLatencySamples;
        public delegate* unmanaged[Cdecl]<void*, ProcessSetup*, int> SetupProcessing;
        public delegate* unmanaged[Cdecl]<void*, byte, int> SetProcessing;
        public delegate* unmanaged[Cdecl]<void*, ProcessData*, int> Process;
        public IntPtr GetTailSamples;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EditControllerVtbl
    {
        public IntPtr QueryInterface, AddRef, Release, Initialize, Terminate, SetComponentState, SetState, GetState;
        public delegate* unmanaged[Cdecl]<void*, int> GetParameterCount;
        public delegate* unmanaged[Cdecl]<void*, int, ParameterInfo*, int> GetParameterInfo;
        public IntPtr GetParamStringByValue, GetParamValueByString, NormalizedParamToPlain, PlainParamToNormalized;
        public delegate* unmanaged[Cdecl]<void*, uint, double> GetParamNormalized;
        public delegate* unmanaged[Cdecl]<void*, uint, double, int> SetParamNormalized;
        public IntPtr SetComponentHandler, CreateView;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PClassInfo
    {
        public fixed byte Cid[16];
        public int Cardinality;
        public fixed byte Category[32];
        public fixed byte Name[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ParameterInfo
    {
        public uint Id;
        public fixed char Title[128];       // VST3 String128 = UTF-16
        public fixed char ShortTitle[128];
        public fixed char Units[128];
        public int StepCount;
        public double DefaultNormalizedValue;
        public int UnitId;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BusInfo
    {
        public int MediaType;
        public int Direction;
        public int ChannelCount;
        public fixed char Name[128];
        public int BusType;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBusBuffers
    {
        public int NumChannels;
        public ulong SilenceFlags;
        public float** ChannelBuffers32;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessSetup
    {
        public int ProcessMode;
        public int SymbolicSampleSize;   // 0 = kSample32
        public int MaxSamplesPerBlock;
        public double SampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessData
    {
        public int ProcessMode;
        public int SymbolicSampleSize;
        public int NumSamples;
        public int NumInputs;
        public int NumOutputs;
        public AudioBusBuffers* Inputs;
        public AudioBusBuffers* Outputs;
        public void* InputParameterChanges;
        public void* OutputParameterChanges;
        public void* InputEvents;
        public void* OutputEvents;
        public void* ProcessContext;
    }

    /// <summary>Read a fixed UTF-16 field (VST3 String128) into a managed string.</summary>
    public static string Utf16(char* p, int capacity)
    {
        var len = 0;
        while (len < capacity && p[len] != 0) len++;
        return new string(p, 0, len);
    }

    /// <summary>Read a fixed ASCII field (PClassInfo name/category) into a managed string.</summary>
    public static string Ascii(byte* p, int capacity)
    {
        var len = 0;
        while (len < capacity && p[len] != 0) len++;
        return len == 0 ? "" : Marshal.PtrToStringAnsi((IntPtr)p, len);
    }
}
