namespace MidiSharp.Hosting.Sandbox;

/// <summary>
/// The wire protocol between <see cref="SandboxedPlugin"/> (host) and the worker process. Control flows
/// over two anonymous pipes (one each direction) as length-prefixed binary; audio crosses through a
/// shared-memory block laid out as four channel regions. Every command gets a single response so the two
/// processes stay in lock-step.
/// </summary>
public static class SandboxProtocol
{
    // host → worker
    public const byte CmdProcess = 0x01;
    public const byte CmdSetParam = 0x02;
    public const byte CmdGetParam = 0x03;
    public const byte CmdReset = 0x04;
    public const byte CmdDispose = 0xFF;

    // worker → host
    public const byte RespReady = 0x10;   // followed by metadata (name, isInstrument, params)
    public const byte RespError = 0x11;   // followed by a message string
    public const byte RespAck = 0x12;
    public const byte RespParamValue = 0x13;
    public const byte RespProcessed = 0x14;

    // scan mode (worker → host, streamed)
    public const byte ScanBegin = 0x1F;        // followed by the file path about to be scanned (for crash-resume)
    public const byte ScanDescriptor = 0x20;   // format, id, name, vendor, isInstrument, path
    public const byte ScanDone = 0x21;

    public const int MaxChannels = 2;

    /// <summary>Shared-memory size for a given max block: in-L, in-R, out-L, out-R, each maxFrames floats.</summary>
    public static long SharedSize(int maxFrames) => (long)MaxChannels * 2 * maxFrames * sizeof(float);

    /// <summary>Byte offset of a channel region in the shared block. region: 0=inL,1=inR,2=outL,3=outR.</summary>
    public static long RegionOffset(int region, int maxFrames) => (long)region * maxFrames * sizeof(float);
}
