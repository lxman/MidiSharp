using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using MidiSharp.Hosting.Clap;
using MidiSharp.Hosting.EditorHost;
using MidiSharp.Hosting.Ladspa;
using MidiSharp.Hosting.Sandbox;
using MidiSharp.Hosting.Vst2;
using MidiSharp.Hosting.Vst3;
using static MidiSharp.Hosting.Sandbox.SandboxProtocol;

// Scan mode: enumerate ONE format file-by-file and stream descriptors back. Before each file it sends a
// BEGIN marker, so if a file crashes this process the host knows which one and restarts us to resume past
// it. A native crash kills only this process; the host keeps whatever streamed.
// args: --scan <format> <outHandle> <resumeAfterPath> [searchPath ...]
if (args.Length >= 4 && args[0] == "--scan")
{
    using var scanPipe = new AnonymousPipeClientStream(PipeDirection.Out, args[2]);
    using var scanWriter = new BinaryWriter(scanPipe);
    var resumeAfter = args[3];
    try
    {
        IPluginFormat sfmt = args[1] switch
        {
            "CLAP" => new ClapFormat(),
            "VST3" => new Vst3Format(),
            "VST2" => new Vst2Format(),
            "LADSPA" => new LadspaFormat(),
            _ => throw new NotSupportedException(args[1]),
        };
        var paths = args.Length > 4 ? args[4..] : sfmt.DefaultSearchPaths;

        var skipping = resumeAfter.Length > 0;   // skip files up to and including resumeAfter (the last crasher)
        foreach (var file in sfmt.EnumerateFiles(paths))
        {
            if (skipping)
            {
                if (file == resumeAfter) skipping = false;
                continue;
            }
            scanWriter.Write(SandboxProtocol.ScanBegin);
            scanWriter.Write(file);
            scanWriter.Flush();                   // announce the file BEFORE touching its native code
            foreach (var d in sfmt.ScanFile(file))
            {
                scanWriter.Write(SandboxProtocol.ScanDescriptor);
                scanWriter.Write(d.Format); scanWriter.Write(d.Id); scanWriter.Write(d.Name);
                scanWriter.Write(d.Vendor); scanWriter.Write(d.IsInstrument); scanWriter.Write(d.Path);
                scanWriter.Flush();
            }
        }
    }
    catch { /* a managed scan error still completes with DONE; a native crash just exits the process */ }
    scanWriter.Write(SandboxProtocol.ScanDone);
    scanWriter.Flush();
    return 0;
}

// args: <inHandle> <outHandle> <mmfPath> <maxFrames> <format> <id> <pluginPath> <sampleRate> <name> <isInstrument>
if (args.Length < 10)
{
    Console.Error.WriteLine("usage: <in> <out> <mmf> <maxFrames> <format> <id> <path> <rate> <name> <isInstrument>");
    return 2;
}

var inHandle = args[0];
var outHandle = args[1];
var mmfPath = args[2];
var maxFrames = int.Parse(args[3]);
var format = args[4];
var id = args[5];
var pluginPath = args[6];
var rate = int.Parse(args[7]);
var name = args[8];
var isInstrument = args[9] == "1";

using var pipeIn = new AnonymousPipeClientStream(PipeDirection.In, inHandle);    // commands from host
using var pipeOut = new AnonymousPipeClientStream(PipeDirection.Out, outHandle); // responses to host
using var reader = new BinaryReader(pipeIn);
using var writer = new BinaryWriter(pipeOut);

var size = SharedSize(maxFrames);
using var mmf = MemoryMappedFile.CreateFromFile(mmfPath, FileMode.Open, null, size, MemoryMappedFileAccess.ReadWrite);
using var view = mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);

IHostedPlugin plugin;
unsafe
{
    byte* basePtr = null;
    view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

    IPluginFormat fmt = format switch
    {
        "CLAP" => new ClapFormat(),
        "VST3" => new Vst3Format(),
        "VST2" => new Vst2Format(),
        "LADSPA" => new LadspaFormat(),
        _ => throw new NotSupportedException($"Unknown plugin format '{format}'."),
    };
    var config = new AudioConfig(rate, maxFrames, ChannelCount: 2);

    try
    {
        plugin = fmt.Load(new PluginDescriptor(format, id, name, "", isInstrument, pluginPath), config);
    }
    catch (Exception ex)
    {
        writer.Write(RespError);
        writer.Write(ex.Message);
        writer.Flush();
        return 1;
    }

    // Ready: hand the host the plugin's metadata, parameters, and whether it has an editor.
    writer.Write(RespReady);
    writer.Write(plugin.Descriptor.Name);
    writer.Write(plugin.IsInstrument);
    writer.Write(plugin.Parameters.Count);
    foreach (var p in plugin.Parameters)
    {
        writer.Write(p.Index);
        writer.Write(p.Name);
        writer.Write(p.MinValue);
        writer.Write(p.MaxValue);
        writer.Write(p.DefaultValue);
    }
    writer.Write(plugin.Gui is { HasEditor: true });
    writer.Flush();

    // The plugin's editor runs HERE, on this command thread — the same thread that created the plugin,
    // which CLAP requires for its GUI calls (they deadlock off the creation thread). While an editor is
    // open we pump its run loop on this thread and let the command pipe wake the poll, so process/param/
    // state commands are still serviced (the proxy is lock-step, so at most one is in flight).
    EditorSession? editor = null;
    var pipeFd = (int)pipeIn.SafePipeHandle.DangerousGetHandle();   // Unix: the pipe handle is its fd
    var closeEditor = false;
    var disposeReq = false;

    // Channel pointers into the shared block (fixed for the session; only the frame count varies).
    var inPtrs = (float**)NativeMemory.Alloc(2, (nuint)IntPtr.Size);
    var outPtrs = (float**)NativeMemory.Alloc(2, (nuint)IntPtr.Size);
    inPtrs[0] = (float*)(basePtr + RegionOffset(0, maxFrames));
    inPtrs[1] = (float*)(basePtr + RegionOffset(1, maxFrames));
    outPtrs[0] = (float*)(basePtr + RegionOffset(2, maxFrames));
    outPtrs[1] = (float*)(basePtr + RegionOffset(3, maxFrames));

    var events = new HostEvent[256];

    // Handle one command. Never destroys an open editor session itself (close/dispose set flags the main
    // loop acts on, so we don't tear down the session we may be pumping inside).
    void Handle(byte cmd)
    {
        if (cmd == CmdProcess)
        {
            var frames = reader.ReadInt32();
            var n = reader.ReadInt32();
            if (n > events.Length) events = new HostEvent[n];
            for (var i = 0; i < n; i++)
                events[i] = new HostEvent
                {
                    SampleOffset = reader.ReadInt32(),
                    Kind = (HostEventKind)reader.ReadByte(),
                    Status = reader.ReadByte(),
                    Data1 = reader.ReadByte(),
                    Data2 = reader.ReadByte(),
                    ParamIndex = reader.ReadInt32(),
                    ParamValue = reader.ReadDouble(),
                };
            var input = new PlanarBuffers(inPtrs, 2, frames);
            var output = new PlanarBuffers(outPtrs, 2, frames);
            plugin.Process(input, output, events.AsSpan(0, n));
            writer.Write(RespProcessed);
            writer.Flush();
        }
        else if (cmd == CmdSetParam)
        {
            var idx = reader.ReadInt32();
            var val = reader.ReadDouble();
            plugin.SetParameter(idx, val);
            writer.Write(RespAck);
            writer.Flush();
        }
        else if (cmd == CmdGetParam)
        {
            var idx = reader.ReadInt32();
            writer.Write(RespParamValue);
            writer.Write(plugin.GetParameter(idx));
            writer.Flush();
        }
        else if (cmd == CmdSaveState)
        {
            var blob = plugin.SaveState();
            writer.Write(RespState);
            writer.Write(blob.Length);
            writer.Write(blob);
            writer.Flush();
        }
        else if (cmd == CmdLoadState)
        {
            var len = reader.ReadInt32();
            var blob = reader.ReadBytes(len);
            plugin.LoadState(blob);
            writer.Write(RespAck);
            writer.Flush();
        }
        else if (cmd == CmdOpenEditor)
        {
            var title = reader.ReadString();
            if (editor is { IsOpen: true }) { writer.Write(RespAck); writer.Write(false); writer.Flush(); return; }  // already open
            editor = EditorSession.Open(plugin.Gui, title);   // on THIS thread — the plugin's creation thread
            var ok = editor is { IsOpen: true };
            if (ok)
                // Wake the editor's poll when a command arrives, and service it on this thread.
                editor!.RunLoop!.RegisterFd(pipeFd, () => { try { Handle(reader.ReadByte()); } catch (EndOfStreamException) { disposeReq = true; } });
            else { editor?.Close(); editor = null; }
            writer.Write(RespAck);
            writer.Write(ok);
            writer.Flush();
        }
        else if (cmd == CmdCloseEditor) { closeEditor = true; writer.Write(RespAck); writer.Flush(); }
        else if (cmd == CmdReset) { writer.Write(RespAck); writer.Flush(); }
        else if (cmd == CmdDispose) disposeReq = true;
    }

    try
    {
        while (!disposeReq)
        {
            if (editor is { IsOpen: true } && !editor.ShouldClose && !closeEditor)
            {
                editor.PumpOnce(20);   // commands arrive via the pipe-fd callback registered above
            }
            else
            {
                if (editor != null) { editor.Close(); editor = null; closeEditor = false; }
                if (disposeReq) break;
                byte cmd;
                try { cmd = reader.ReadByte(); }
                catch (EndOfStreamException) { break; }   // host went away
                Handle(cmd);
            }
        }
    }
    finally
    {
        editor?.Close();
        NativeMemory.Free(inPtrs);
        NativeMemory.Free(outPtrs);
        view.SafeMemoryMappedViewHandle.ReleasePointer();
    }
}

plugin.Dispose();
return 0;
