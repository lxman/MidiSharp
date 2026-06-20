using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.Clap;

/// <summary>
/// The CLAP ABI (clap 1.x), transcribed from the free-audio/clap headers. CLAP is a pure C ABI, so the
/// structs are mirrored field-for-field as blittable layouts with native function-pointer fields
/// (<c>delegate* unmanaged[Cdecl]</c>); fields the host never calls are kept as <see cref="IntPtr"/> so
/// offsets stay exact without binding signatures we don't use. C <c>bool</c> is one byte, modelled as
/// <see cref="byte"/>; <c>clap_id</c> is <see cref="uint"/>.
/// </summary>
internal static class ClapAbi
{
    public const string EntryExport = "clap_entry";
    public const string FactoryId = "clap.plugin-factory";
    public const string ExtAudioPorts = "clap.audio-ports";
    public const string ExtParams = "clap.params";
    public const string ExtState = "clap.state";
    public const string ExtGui = "clap.gui";

    public const string WindowApiX11 = "x11";
    public const string WindowApiWin32 = "win32";
    public const string WindowApiCocoa = "cocoa";

    public const ushort CoreEventSpaceId = 0;
    public const ushort EventParamValue = 5;
    public const ushort EventMidi = 10;

    public const int ProcessError = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapVersion
    {
        public uint Major, Minor, Revision;
        public static ClapVersion Current => new() { Major = 1, Minor = 2, Revision = 6 };
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPluginEntry
    {
        public ClapVersion Version;
        public delegate* unmanaged[Cdecl]<byte*, byte> Init;        // bool init(const char* path)
        public IntPtr Deinit;                                        // void deinit(void)
        public delegate* unmanaged[Cdecl]<byte*, void*> GetFactory;  // const void* get_factory(const char* id)
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPluginFactory
    {
        public delegate* unmanaged[Cdecl]<ClapPluginFactory*, uint> GetPluginCount;
        public delegate* unmanaged[Cdecl]<ClapPluginFactory*, uint, ClapPluginDescriptor*> GetPluginDescriptor;
        public delegate* unmanaged[Cdecl]<ClapPluginFactory*, ClapHost*, byte*, ClapPlugin*> CreatePlugin;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPluginDescriptor
    {
        public ClapVersion Version;
        public byte* Id, Name, Vendor, Url, ManualUrl, SupportUrl, PluginVersion, Description;
        public byte** Features;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPlugin
    {
        public ClapPluginDescriptor* Desc;
        public void* PluginData;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> Init;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> Destroy;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, double, uint, uint, byte> Activate;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> Deactivate;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> StartProcessing;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> StopProcessing;
        public IntPtr Reset;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapProcess*, int> Process;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, void*> GetExtension;
        public IntPtr OnMainThread;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapHost
    {
        public ClapVersion Version;
        public void* HostData;
        public byte* Name, Vendor, Url, HostVersion;
        public delegate* unmanaged[Cdecl]<ClapHost*, byte*, void*> GetExtension;
        public delegate* unmanaged[Cdecl]<ClapHost*, void> RequestRestart;
        public delegate* unmanaged[Cdecl]<ClapHost*, void> RequestProcess;
        public delegate* unmanaged[Cdecl]<ClapHost*, void> RequestCallback;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapAudioBuffer
    {
        public float** Data32;
        public double** Data64;
        public uint ChannelCount;
        public uint Latency;
        public ulong ConstantMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapProcess
    {
        public long SteadyTime;
        public uint FramesCount;
        public void* Transport;
        public ClapAudioBuffer* AudioInputs;
        public ClapAudioBuffer* AudioOutputs;
        public uint AudioInputsCount;
        public uint AudioOutputsCount;
        public void* InEvents;     // const clap_input_events_t*
        public void* OutEvents;    // const clap_output_events_t*
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapInputEvents
    {
        public void* Ctx;
        public delegate* unmanaged[Cdecl]<ClapInputEvents*, uint> Size;
        public delegate* unmanaged[Cdecl]<ClapInputEvents*, uint, void*> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapOutputEvents
    {
        public void* Ctx;
        public delegate* unmanaged[Cdecl]<ClapOutputEvents*, void*, byte> TryPush;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapAudioPorts
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte, uint> Count;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, byte, ClapAudioPortInfo*, byte> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapAudioPortInfo
    {
        public uint Id;
        public fixed byte Name[256];   // CLAP_NAME_SIZE
        public uint Flags;
        public uint ChannelCount;
        public byte* PortType;
        public uint InPlacePair;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPluginParams
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint> Count;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, ClapParamInfo*, byte> GetInfo;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, double*, byte> GetValue;
        public IntPtr ValueToText;
        public IntPtr TextToValue;
        public IntPtr Flush;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapParamInfo
    {
        public uint Id;
        public uint Flags;
        public void* Cookie;
        public fixed byte Name[256];    // CLAP_NAME_SIZE
        public fixed byte Module[1024]; // CLAP_PATH_SIZE
        public double MinValue, MaxValue, DefaultValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClapEventHeader
    {
        public uint Size;
        public uint Time;
        public ushort SpaceId;
        public ushort Type;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapOStream
    {
        public void* Ctx;
        public delegate* unmanaged[Cdecl]<ClapOStream*, void*, ulong, long> Write;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapIStream
    {
        public void* Ctx;
        public delegate* unmanaged[Cdecl]<ClapIStream*, void*, ulong, long> Read;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPluginState
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapOStream*, byte> Save;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapIStream*, byte> Load;
    }

    // clap_window: { const char* api; union { x11 (unsigned long) | win32/cocoa/ptr (void*) } }. The union
    // is pointer-sized; an X11 XID (unsigned long) fits in the same 8 bytes.
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapWindow
    {
        public byte* Api;
        public nuint Handle;   // x11 XID, or a void* for win32/cocoa
    }

    // clap_plugin_gui, in vtable order. Methods are [main-thread].
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPluginGui
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, byte, byte> IsApiSupported;       // (plugin, api, is_floating)
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte**, byte*, byte> GetPreferredApi;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, byte, byte> Create;               // (plugin, api, is_floating)
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> Destroy;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, double, byte> SetScale;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint*, uint*, byte> GetSize;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> CanResize;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void*, byte> GetResizeHints;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint*, uint*, byte> AdjustSize;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, uint, byte> SetSize;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapWindow*, byte> SetParent;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapWindow*, byte> SetTransient;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, void> SuggestTitle;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> Show;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> Hide;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapEventParamValue
    {
        public ClapEventHeader Header;
        public uint ParamId;
        public void* Cookie;
        public int NoteId;
        public short PortIndex;
        public short Channel;
        public short Key;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapEventMidi
    {
        public ClapEventHeader Header;
        public ushort PortIndex;
        public byte Data0;
        public byte Data1;
        public byte Data2;
    }

    /// <summary>Marshal a NUL-terminated UTF-8 C string to managed, or "" when null.</summary>
    public static unsafe string Str(byte* p) => p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "";

    /// <summary>Marshal a fixed inline UTF-8 byte buffer (e.g. clap name[256]) to managed.</summary>
    public static unsafe string FixedStr(byte* p, int capacity)
    {
        var len = 0;
        while (len < capacity && p[len] != 0) len++;
        return len == 0 ? "" : Marshal.PtrToStringUTF8((IntPtr)p, len);
    }
}
