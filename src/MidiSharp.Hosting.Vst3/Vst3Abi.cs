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
    public const int ResultOk = 0;          // kResultOk == kResultTrue == 0 on every platform
    public const int ResultFalse = 1;       // kResultFalse == 1 on every platform
    public static bool Ok(int r) => r == ResultOk;

    // kNoInterface differs by platform: Windows VST3 is COM-compatible (HRESULT E_NOINTERFACE), the rest
    // use -1. Our host-side COM objects must return the right one from queryInterface.
    public static readonly int NoInterface = OperatingSystem.IsWindows() ? unchecked((int)0x80004002) : -1;

    public static readonly string[] FactoryExport = ["GetPluginFactory"];
    public const string AudioModuleCategory = "Audio Module Class";

    public const int kNoteOnEvent = 0;
    public const int kNoteOffEvent = 1;
    public const int kIBSeekSet = 0;   // IBStream seek modes
    public const string PlatformTypeX11 = "X11EmbedWindowID";

    // ── IIDs (non-Windows UID layout: each of the four uint32 stored big-endian) ──
    public static readonly byte[] IidFUnknown = Uid(0x00000000, 0x00000000, 0xC0000000, 0x00000046);
    public static readonly byte[] IidPluginFactory = Uid(0x7A4D811C, 0x52114A1F, 0xAED9D2EE, 0x0B43BF9F);
    public static readonly byte[] IidPluginFactory2 = Uid(0x0007B650, 0xF24B4C0B, 0xA464EDB9, 0xF00B2ABB);
    public static readonly byte[] IidComponent = Uid(0xE831FF31, 0xF2D54301, 0x928EBBEE, 0x25697802);
    public static readonly byte[] IidAudioProcessor = Uid(0x42043F99, 0xB7DA453C, 0xA569E79D, 0x9AAEC33D);
    public static readonly byte[] IidEditController = Uid(0xDCD7BBE3, 0x7742448D, 0xA874AACC, 0x979C759E);
    public static readonly byte[] IidHostApplication = Uid(0x58E595CC, 0xDB2D4969, 0x8B6AAF8C, 0x36A664E5);
    public static readonly byte[] IidComponentHandler = Uid(0x93A0BEA3, 0x0BD045DB, 0x8E890B0C, 0xC1E46AC6);
    public static readonly byte[] IidBStream = Uid(0xC3BF6EA2, 0x30994752, 0x9B6BF990, 0x1EE33E9B);
    public static readonly byte[] IidEventList = Uid(0x3A2C4214, 0x346349FE, 0xB2C4F397, 0xB9695A44);
    public static readonly byte[] IidConnectionPoint = Uid(0x70A4156F, 0x6E6E4026, 0x989148BF, 0xAA60D8D1);
    public static readonly byte[] IidPlugView = Uid(0x5BC32507, 0xD06049EA, 0xA6151B52, 0x2B755B29);
    public static readonly byte[] IidPlugFrame = Uid(0x367FAF01, 0xAFA94693, 0x8D4DA2A0, 0xED0882A3);
    public static readonly byte[] IidRunLoop = Uid(0x18C35366, 0x97764F1A, 0x9C5B8385, 0x7A871389);
    public static readonly byte[] IidEventHandler = Uid(0x561E65C9, 0x13A0496F, 0x813A2C35, 0x654D7983);
    public static readonly byte[] IidTimerHandler = Uid(0x10BDD94F, 0x41424774, 0x821FAD8F, 0xECA72CA9);

    // Lay out a TUID from four uint32s the way the VST3 SMTG_INLINE_UID macro does — which differs by
    // platform. Windows is COM-compatible (the GUID layout: first uint32 little-endian, second as two
    // little-endian uint16s, last two big-endian); every other platform stores all four big-endian.
    public static byte[] Uid(uint a, uint b, uint c, uint d) =>
        OperatingSystem.IsWindows()
        ?
        [
            (byte)a, (byte)(a >> 8), (byte)(a >> 16), (byte)(a >> 24),
            (byte)(b >> 16), (byte)(b >> 24), (byte)b, (byte)(b >> 8),
            (byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c,
            (byte)(d >> 24), (byte)(d >> 16), (byte)(d >> 8), (byte)d,
        ]
        :
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
        public delegate* unmanaged[Cdecl]<void*, byte*, int> GetControllerClassId;   // writes a TUID
        public IntPtr SetIoMode;
        public delegate* unmanaged[Cdecl]<void*, int, int, int> GetBusCount;
        public delegate* unmanaged[Cdecl]<void*, int, int, int, BusInfo*, int> GetBusInfo;
        public IntPtr GetRoutingInfo;
        public delegate* unmanaged[Cdecl]<void*, int, int, int, byte, int> ActivateBus;
        public delegate* unmanaged[Cdecl]<void*, byte, int> SetActive;
        public delegate* unmanaged[Cdecl]<void*, void*, int> SetState, GetState;   // IBStream
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
        public delegate* unmanaged[Cdecl]<void*, byte*, void**, int> QueryInterface;
        public IntPtr AddRef;
        public delegate* unmanaged[Cdecl]<void*, uint> Release;
        public delegate* unmanaged[Cdecl]<void*, void*, int> Initialize;
        public delegate* unmanaged[Cdecl]<void*, int> Terminate;
        public delegate* unmanaged[Cdecl]<void*, void*, int> SetComponentState;   // IBStream (sync params to component)
        public delegate* unmanaged[Cdecl]<void*, void*, int> SetState, GetState;  // controller's own IBStream state
        public delegate* unmanaged[Cdecl]<void*, int> GetParameterCount;
        public delegate* unmanaged[Cdecl]<void*, int, ParameterInfo*, int> GetParameterInfo;
        public IntPtr GetParamStringByValue, GetParamValueByString, NormalizedParamToPlain, PlainParamToNormalized;
        public delegate* unmanaged[Cdecl]<void*, uint, double> GetParamNormalized;
        public delegate* unmanaged[Cdecl]<void*, uint, double, int> SetParamNormalized;
        public delegate* unmanaged[Cdecl]<void*, void*, int> SetComponentHandler;
        public delegate* unmanaged[Cdecl]<void*, byte*, void*> CreateView;   // returns IPlugView*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ViewRect { public int Left, Top, Right, Bottom; }

    // IPlugView, in vtable order. Editor calls are main-thread.
    [StructLayout(LayoutKind.Sequential)]
    public struct PlugViewVtbl
    {
        public IntPtr QueryInterface, AddRef;
        public delegate* unmanaged[Cdecl]<void*, uint> Release;
        public delegate* unmanaged[Cdecl]<void*, byte*, int> IsPlatformTypeSupported;
        public delegate* unmanaged[Cdecl]<void*, void*, byte*, int> Attached;
        public delegate* unmanaged[Cdecl]<void*, int> Removed;
        public IntPtr OnWheel, OnKeyDown, OnKeyUp;
        public delegate* unmanaged[Cdecl]<void*, ViewRect*, int> GetSize;
        public delegate* unmanaged[Cdecl]<void*, ViewRect*, int> OnSize;
        public IntPtr OnFocus;
        public delegate* unmanaged[Cdecl]<void*, void*, int> SetFrame;
        public IntPtr CanResize, CheckSizeConstraint;
    }

    // IPluginFactory2 — adds getClassInfo2 (PClassInfo2 carries subCategories, where "Instrument" lives).
    [StructLayout(LayoutKind.Sequential)]
    public struct Factory2Vtbl
    {
        public IntPtr QueryInterface, AddRef, Release, GetFactoryInfo, CountClasses, GetClassInfo;
        public delegate* unmanaged[Cdecl]<void*, byte*, byte*, void**, int> CreateInstance;
        public delegate* unmanaged[Cdecl]<void*, int, PClassInfo2*, int> GetClassInfo2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ConnectionPointVtbl
    {
        public IntPtr QueryInterface, AddRef, Release;
        public delegate* unmanaged[Cdecl]<void*, void*, int> Connect, Disconnect;
        public IntPtr Notify;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PClassInfo2
    {
        public fixed byte Cid[16];
        public int Cardinality;
        public fixed byte Category[32];
        public fixed byte Name[64];
        public uint ClassFlags;
        public fixed byte SubCategories[128];
        public fixed byte Vendor[64];
        public fixed byte Version[64];
        public fixed byte SdkVersion[64];
    }

    // One VST3 event (kEvent, 48 bytes; union at offset 24). Explicit layout so note-on / note-off members
    // overlay the union exactly as the C struct does (NoteOff orders velocity/noteId/tuning differently).
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct VstEvent
    {
        [FieldOffset(0)] public int BusIndex;
        [FieldOffset(4)] public int SampleOffset;
        [FieldOffset(8)] public double PpqPosition;
        [FieldOffset(16)] public ushort Flags;
        [FieldOffset(18)] public ushort Type;
        // NoteOnEvent { int16 channel, int16 pitch, float tuning, float velocity, int32 length, int32 noteId }
        [FieldOffset(24)] public short OnChannel;
        [FieldOffset(26)] public short OnPitch;
        [FieldOffset(28)] public float OnTuning;
        [FieldOffset(32)] public float OnVelocity;
        [FieldOffset(36)] public int OnLength;
        [FieldOffset(40)] public int OnNoteId;
        // NoteOffEvent { int16 channel, int16 pitch, float velocity, int32 noteId, float tuning }
        [FieldOffset(24)] public short OffChannel;
        [FieldOffset(26)] public short OffPitch;
        [FieldOffset(28)] public float OffVelocity;
        [FieldOffset(32)] public int OffNoteId;
        [FieldOffset(36)] public float OffTuning;
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
