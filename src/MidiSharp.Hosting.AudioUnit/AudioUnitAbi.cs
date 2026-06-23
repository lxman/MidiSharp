using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting.AudioUnit;

/// <summary>
/// The AudioToolbox (AU v2) interop slice: the C structs, the <c>AudioComponent</c>/<c>AudioUnit</c> P/Invokes,
/// and the property/scope/format constants the adapter speaks. Pure interop, no logic.
/// </summary>
/// <remarks>
/// Constants and struct layouts here were verified against the live AudioToolbox runtime by the Plan A Task 0
/// render-shim spike (arm64): a stereo <b>non-interleaved float32</b> stream rendered correctly through
/// <c>AULowpass</c>. Audio is non-interleaved, so each channel is its own <see cref="AudioBuffer"/> and the
/// adapter points them straight at the engine's planar channels. Parameter, state, MIDI, and editor ABI are
/// added by their own tasks (Plan A Tasks 4–5, Plan B, Plan C).
/// </remarks>
internal static unsafe class AudioUnitAbi
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    /// <summary>Pack a four-character code (e.g. <c>"aufx"</c>) into its big-endian <c>OSType</c>.</summary>
    public static uint FourCC(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    // ── Component types (OSType) and the Apple manufacturer ──
    public static readonly uint TypeEffect = FourCC("aufx");       // an effect (audio in → audio out)
    public static readonly uint TypeMusicEffect = FourCC("aumf");  // an effect that also takes MIDI
    public static readonly uint TypeMusicDevice = FourCC("aumu");  // an instrument (MIDI → audio)
    public static readonly uint TypeGenerator = FourCC("augn");    // a generator (audio out, no input)
    public static readonly uint ManufacturerApple = FourCC("appl");
    public static readonly uint FormatLinearPcm = FourCC("lpcm");

    /// <summary>True for sound sources driven by MIDI rather than an audio input bus.</summary>
    public static bool IsInstrumentType(uint componentType) =>
        componentType == TypeMusicDevice || componentType == TypeGenerator;

    // ── Property ids (kAudioUnitProperty_*) — spike-verified values ──
    public const uint PropClassInfo = 0;             // CFPropertyListRef — opaque state (Task 5)
    public const uint PropParameterList = 3;         // AudioUnitParameterID[] (Task 4)
    public const uint PropParameterInfo = 4;         // AudioUnitParameterInfo (Task 4)
    public const uint PropStreamFormat = 8;          // AudioStreamBasicDescription
    public const uint PropElementCount = 11;
    public const uint PropMaximumFramesPerSlice = 14;
    public const uint PropSetRenderCallback = 23;    // AURenderCallbackStruct (input pull source)
    public const uint PropCocoaUI = 31;              // AudioUnitCocoaViewInfo (Plan C)

    // ── Scopes (kAudioUnitScope_*) ──
    public const uint ScopeGlobal = 0;
    public const uint ScopeInput = 1;
    public const uint ScopeOutput = 2;

    // ── ASBD format flags: native packed non-interleaved float32 = IsFloat|IsPacked|IsNonInterleaved (=41) ──
    public const uint FormatFlagIsFloat = 1u << 0;
    public const uint FormatFlagIsPacked = 1u << 3;
    public const uint FormatFlagIsNonInterleaved = 1u << 5;
    public const uint FormatFlagsNativeFloatNonInterleaved =
        FormatFlagIsFloat | FormatFlagIsPacked | FormatFlagIsNonInterleaved;

    // ── AudioTimeStamp.mFlags ──
    public const uint TimeStampSampleTimeValid = 1u << 0;

    // ── structs ──
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioComponentDescription
    {
        public uint ComponentType, ComponentSubType, ComponentManufacturer, ComponentFlags, ComponentFlagsMask;
    }

    /// <summary>For non-interleaved float32 the ASBD describes ONE channel: bytes-per-frame/packet = 4,
    /// frames-per-packet = 1, channels-per-frame = the channel count.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public void* Data;
    }

    /// <summary>The stereo specialization of CoreAudio's variable-length <c>AudioBufferList</c>
    /// (<c>{ UInt32 mNumberBuffers; AudioBuffer mBuffers[1]; }</c>) — two inline <see cref="AudioBuffer"/>s for
    /// our non-interleaved stereo bus. The 4 bytes of tail padding after <see cref="NumberBuffers"/> match C's
    /// natural 8-byte alignment of the buffer array (spike-confirmed).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StereoBufferList
    {
        public uint NumberBuffers;
        public AudioBuffer Buffer0;
        public AudioBuffer Buffer1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SmpteTime
    {
        public short Subframes, SubframeDivisor;
        public uint Counter, Type, Flags;
        public short Hours, Minutes, Seconds, Frames;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioTimeStamp
    {
        public double SampleTime;
        public ulong HostTime;
        public double RateScalar;
        public ulong WordClockTime;
        public SmpteTime Smpte;
        public uint Flags, Reserved;
    }

    /// <summary><c>AURenderCallbackStruct</c>: the host's input "pull" source. <c>InputProc</c> signature is
    /// <c>OSStatus (*)(void* refCon, AudioUnitRenderActionFlags* flags, AudioTimeStamp* ts, UInt32 bus,
    /// UInt32 frames, AudioBufferList* ioData)</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AURenderCallbackStruct
    {
        public delegate* unmanaged[Cdecl]<void*, uint*, AudioTimeStamp*, uint, uint, void*, int> InputProc;
        public void* InputProcRefCon;
    }

    // ── parameters (Task 4) ──
    // AudioUnitParameterUnit values used to flag stepped controls.
    public const uint ParamUnitIndexed = 1;   // a list/menu (discrete)
    public const uint ParamUnitBoolean = 2;   // on/off

    /// <summary><c>AudioUnitParameterInfo</c>. Natural C alignment: the <c>char[52]</c> name is followed by 4
    /// bytes of padding so <c>UnitName</c> (a pointer) lands 8-aligned — declared verbatim so the runtime lays
    /// it out identically.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioUnitParameterInfo
    {
        public fixed byte Name[52];
        public IntPtr UnitName;        // CFStringRef
        public uint ClumpId;
        public IntPtr CfNameString;    // CFStringRef (preferred display name)
        public uint Unit;
        public float MinValue, MaxValue, DefaultValue;
        public uint Flags;
    }

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitGetParameter(IntPtr inUnit, uint inId, uint inScope, uint inElement, float* outValue);

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitSetParameter(IntPtr inUnit, uint inId, uint inScope, uint inElement, float inValue, uint inBufferOffsetInFrames);

    // ── AudioComponentFlags (the registry's per-component capability bits, in AudioComponentDescription.ComponentFlags) ──
    // An AU v3 component is delivered by Apple's bridge; v3 effects are typically out-of-process-only and require
    // the async AudioComponentInstantiate path. (Values from AudioComponent.h; spike-verified against real v3 AUs.)
    public const uint CompFlagSandboxSafe = 2;                 // kAudioComponentFlag_SandboxSafe
    public const uint CompFlagIsV3AudioUnit = 4;              // kAudioComponentFlag_IsV3AudioUnit
    public const uint CompFlagRequiresAsync = 8;              // kAudioComponentFlag_RequiresAsyncInstantiation
    public const uint CompFlagCanLoadInProcess = 0x10;       // kAudioComponentFlag_CanLoadInProcess

    // ── AudioComponentInstantiationOptions (the async-load placement; honor the component's CanLoadInProcess bit) ──
    public const uint InstantiationLoadInProcess = 1;        // kAudioComponentInstantiation_LoadInProcess
    public const uint InstantiationLoadOutOfProcess = 2;     // kAudioComponentInstantiation_LoadOutOfProcess

    // ── discovery / instantiation ──
    [DllImport(AudioToolbox)]
    public static extern IntPtr AudioComponentFindNext(IntPtr inComponent, AudioComponentDescription* inDesc);

    [DllImport(AudioToolbox)]
    public static extern int AudioComponentCopyName(IntPtr inComponent, out IntPtr outName /* CFStringRef */);

    [DllImport(AudioToolbox)]
    public static extern int AudioComponentGetDescription(IntPtr inComponent, AudioComponentDescription* outDesc);

    [DllImport(AudioToolbox)]
    public static extern int AudioComponentInstanceNew(IntPtr inComponent, out IntPtr outInstance);

    [DllImport(AudioToolbox)]
    public static extern int AudioComponentInstanceDispose(IntPtr inInstance);

    // ── unit lifecycle / properties / render ──
    [DllImport(AudioToolbox)]
    public static extern int AudioUnitInitialize(IntPtr inUnit);

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitUninitialize(IntPtr inUnit);

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitReset(IntPtr inUnit, uint inScope, uint inElement);

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitSetProperty(IntPtr inUnit, uint inId, uint inScope, uint inElement, void* inData, uint inDataSize);

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitGetProperty(IntPtr inUnit, uint inId, uint inScope, uint inElement, void* outData, uint* ioDataSize);

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitGetPropertyInfo(IntPtr inUnit, uint inId, uint inScope, uint inElement, uint* outDataSize, byte* outWritable);

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitRender(IntPtr inUnit, uint* ioActionFlags, AudioTimeStamp* inTimeStamp, uint inOutputBusNumber, uint inNumberFrames, void* ioData);

    // ── instruments (Plan B) ── deliver a short MIDI message to a music-device AU, sample-accurate within the block.
    [DllImport(AudioToolbox)]
    public static extern int MusicDeviceMIDIEvent(IntPtr inUnit, uint inStatus, uint inData1, uint inData2, uint inOffsetSampleFrame);
}
