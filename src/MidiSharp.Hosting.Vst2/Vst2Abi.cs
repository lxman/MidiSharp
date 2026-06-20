using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.Vst2;

/// <summary>
/// The VST2 ABI — the <c>AEffect</c> struct of function pointers, the host <c>audioMaster</c> callback
/// signature, and the opcode/flag constants — transcribed clean-room (no Steinberg SDK headers). VST2
/// audio is planar <c>float**</c> (like CLAP); parameters are plain 0..1 floats via
/// <c>setParameter</c>/<c>getParameter</c>; MIDI/events go to the plugin via the <c>effProcessEvents</c>
/// dispatcher opcode carrying <c>VstEvents</c> with per-event <c>deltaFrames</c>.
/// </summary>
internal static class Vst2Abi
{
    public const int EffectMagic = 0x56737450;   // 'VstP'
    public static readonly string[] EntryExports = ["VSTPluginMain", "main"];

    // Plugin dispatcher opcodes (effXxx).
    public const int EffOpen = 0;
    public const int EffClose = 1;
    public const int EffSetProgram = 2;
    public const int EffGetParamLabel = 6;
    public const int EffGetParamDisplay = 7;
    public const int EffGetParamName = 8;
    public const int EffSetSampleRate = 10;
    public const int EffSetBlockSize = 11;
    public const int EffMainsChanged = 12;   // value: 1 = resume, 0 = suspend
    public const int EffGetChunk = 23;
    public const int EffSetChunk = 24;
    public const int EffProcessEvents = 25;
    public const int EffGetPlugCategory = 35;
    public const int EffGetEffectName = 45;
    public const int EffGetVendorString = 47;
    public const int EffGetProductString = 48;
    public const int EffCanDo = 51;
    public const int EffGetVstVersion = 58;

    // AEffect.flags
    public const int FlagsCanReplacing = 1 << 4;   // 16
    public const int FlagsProgramChunks = 1 << 5;  // 32
    public const int FlagsIsSynth = 1 << 8;        // 256

    // Host callback opcodes (audioMasterXxx).
    public const int AmVersion = 1;
    public const int AmCurrentId = 2;
    public const int AmIdle = 3;
    public const int AmGetTime = 7;
    public const int AmGetSampleRate = 16;
    public const int AmGetBlockSize = 17;
    public const int AmGetCurrentProcessLevel = 23;
    public const int AmGetVendorString = 32;
    public const int AmGetProductString = 33;
    public const int AmGetVendorVersion = 34;
    public const int AmCanDo = 37;

    public const int VstMidiType = 1;

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct AEffect
    {
        public int Magic;
        public delegate* unmanaged[Cdecl]<AEffect*, int, int, IntPtr, void*, float, IntPtr> Dispatcher;
        public IntPtr Process;   // deprecated accumulating process — unused
        public delegate* unmanaged[Cdecl]<AEffect*, int, float, void> SetParameter;
        public delegate* unmanaged[Cdecl]<AEffect*, int, float> GetParameter;
        public int NumPrograms;
        public int NumParams;
        public int NumInputs;
        public int NumOutputs;
        public int Flags;
        public IntPtr Resvd1;
        public IntPtr Resvd2;
        public int InitialDelay;
        public int RealQualities;   // deprecated
        public int OffQualities;    // deprecated
        public float IoRatio;       // deprecated
        public void* Object;
        public void* User;
        public int UniqueID;
        public int Version;
        public delegate* unmanaged[Cdecl]<AEffect*, float**, float**, int, void> ProcessReplacing;
        public IntPtr ProcessDoubleReplacing;
        public fixed byte Future[56];
    }

    // The host-provided entry point: AEffect* VSTPluginMain(audioMasterCallback host).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate AEffect* VstPluginMainFn(IntPtr audioMaster);

    [StructLayout(LayoutKind.Sequential)]
    public struct VstMidiEvent
    {
        public int Type;          // VstMidiType
        public int ByteSize;      // sizeof(VstMidiEvent)
        public int DeltaFrames;   // sample offset within the block
        public int Flags;
        public int NoteLength;
        public int NoteOffset;
        public byte Midi0, Midi1, Midi2, Midi3;
        public byte Detune;
        public byte NoteOffVelocity;
        public byte Reserved1, Reserved2;
    }

    // VstEvents has a variable-length VstEvent* array; we build it by hand in unmanaged memory:
    // [ int numEvents | pad | IntPtr reserved | IntPtr events[0..numEvents-1] ].
    public const int VstEventsHeaderBytes = 16;   // numEvents(4)+pad(4)+reserved(8)

    public static unsafe string ReadCString(byte* p, int capacity)
    {
        var len = 0;
        while (len < capacity && p[len] != 0) len++;
        return len == 0 ? "" : Marshal.PtrToStringAnsi((IntPtr)p, len);
    }
}
