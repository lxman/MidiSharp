using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Threading;
using SysProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using static MidiSharp.Hosting.Sandbox.SandboxProtocol;

namespace MidiSharp.Hosting.Sandbox;

/// <summary>
/// An <see cref="IHostedPlugin"/> that runs the real plugin in a separate worker process. Audio crosses
/// through a shared-memory block and control over two anonymous pipes; from the engine's side it behaves
/// like any hosted plugin. If the worker dies — a plugin segfault, a hang past the timeout — the proxy
/// latches "dead", every subsequent <see cref="Process"/> emits silence, and the host process survives.
/// </summary>
public sealed unsafe class SandboxedPlugin : IHostedPlugin
{
    private readonly int _maxFrames;
    private readonly SysProcess _worker;
    private readonly AnonymousPipeServerStream _toWorker;
    private readonly AnonymousPipeServerStream _fromWorker;
    private readonly BinaryWriter _writer;
    private readonly BinaryReader _reader;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly string _mmfPath;
    private byte* _base;

    private readonly List<PluginParameter> _parameters = [];
    private volatile bool _dead;
    private volatile bool _disposed;
    private readonly object _lock = new();

    // Watchdog: each request arms a deadline; a background thread kills the worker if it blows it (a hung
    // plugin), which unblocks the request's pipe read so it recovers as a dead insert.
    private const int LoadTimeoutMs = 10_000;     // plugins can be slow to load/activate
    private const int ProcessTimeoutMs = 500;     // one block is a few ms; >this means wedged
    private const int ControlTimeoutMs = 1_000;   // get/set parameter
    private readonly System.Threading.Thread _watchdog;
    private volatile bool _watchdogStop;
    private long _deadline;   // Environment.TickCount64 by which the in-flight request must answer; 0 = idle

    public PluginDescriptor Descriptor { get; }
    public bool IsInstrument { get; }
    public IReadOnlyList<PluginParameter> Parameters => _parameters;

    /// <summary>True once the worker has died; the insert is producing silence.</summary>
    public bool IsDead => _dead;

    /// <param name="descriptor">The plugin to host (format, id, path).</param>
    /// <param name="workerDll">Path to MidiSharp.Hosting.Worker.dll (run via <c>dotnet</c>).</param>
    public SandboxedPlugin(PluginDescriptor descriptor, string workerDll, AudioConfig config)
    {
        _maxFrames = config.MaxBlockFrames;
        _mmfPath = Path.Combine(Path.GetTempPath(), "midisharp-sbx-" + Guid.NewGuid().ToString("N") + ".bin");

        var size = SharedSize(_maxFrames);
        using (var fs = new FileStream(_mmfPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.SetLength(size);
        _mmf = MemoryMappedFile.CreateFromFile(_mmfPath, FileMode.Open, null, size, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);

        _toWorker = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        _fromWorker = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(workerDll);
        psi.ArgumentList.Add(_toWorker.GetClientHandleAsString());     // worker reads commands here
        psi.ArgumentList.Add(_fromWorker.GetClientHandleAsString());   // worker writes responses here
        psi.ArgumentList.Add(_mmfPath);
        psi.ArgumentList.Add(_maxFrames.ToString());
        psi.ArgumentList.Add(descriptor.Format);
        psi.ArgumentList.Add(descriptor.Id);
        psi.ArgumentList.Add(descriptor.Path);
        psi.ArgumentList.Add(config.SampleRate.ToString());
        psi.ArgumentList.Add(descriptor.Name);
        psi.ArgumentList.Add(descriptor.IsInstrument ? "1" : "0");

        _worker = SysProcess.Start(psi) ?? throw new InvalidOperationException("Failed to start sandbox worker.");
        _toWorker.DisposeLocalCopyOfClientHandle();
        _fromWorker.DisposeLocalCopyOfClientHandle();
        _writer = new BinaryWriter(_toWorker);
        _reader = new BinaryReader(_fromWorker);

        _watchdog = new System.Threading.Thread(WatchdogLoop) { IsBackground = true, Name = "midisharp-sandbox-watchdog" };
        _watchdog.Start();

        // Read the worker's ready message (or an error), bounded by the load timeout (a hung load → throw).
        Arm(LoadTimeoutMs);
        try
        {
            var tag = _reader.ReadByte();
            if (tag == RespError) throw new InvalidOperationException("Sandbox worker failed to load plugin: " + _reader.ReadString());
            if (tag != RespReady) throw new InvalidOperationException($"Unexpected sandbox handshake byte 0x{tag:X2}.");
            var name = _reader.ReadString();
            IsInstrument = _reader.ReadBoolean();
            var count = _reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var idx = _reader.ReadInt32();
                var pname = _reader.ReadString();
                var min = _reader.ReadDouble();
                var max = _reader.ReadDouble();
                var def = _reader.ReadDouble();
                _parameters.Add(new PluginParameter(idx, pname, "", min, max, def));
            }
            Descriptor = descriptor with { Name = name };
        }
        catch (EndOfStreamException) { throw new InvalidOperationException("Sandbox worker exited or hung during startup."); }
        finally { Disarm(); }
    }

    private void WatchdogLoop()
    {
        while (!_watchdogStop)
        {
            var d = Volatile.Read(ref _deadline);
            if (d != 0 && Environment.TickCount64 > d) Die();   // request overran its deadline → kill the hung worker
            System.Threading.Thread.Sleep(25);
        }
    }

    private void Arm(int ms) => Volatile.Write(ref _deadline, Environment.TickCount64 + ms);
    private void Disarm() => Volatile.Write(ref _deadline, 0);

    public void Activate(AudioConfig config) { }   // the worker activated the plugin at startup
    public void Deactivate() { }

    public void Process(PlanarBuffers input, PlanarBuffers output, ReadOnlySpan<HostEvent> events)
    {
        var frames = Math.Min(output.Frames, _maxFrames);
        if (_dead || _disposed) { Silence(output, frames); return; }

        lock (_lock)
        {
            try
            {
                CopyIn(input, 0, frames);
                CopyIn(input, 1, frames);
                _writer.Write(CmdProcess);
                _writer.Write(frames);
                _writer.Write(events.Length);
                foreach (var e in events)
                {
                    _writer.Write(e.SampleOffset);
                    _writer.Write((byte)e.Kind);
                    _writer.Write(e.Status); _writer.Write(e.Data1); _writer.Write(e.Data2);
                    _writer.Write(e.ParamIndex); _writer.Write(e.ParamValue);
                }
                _writer.Flush();
                Arm(ProcessTimeoutMs);
                var ok = _reader.ReadByte() == RespProcessed;   // watchdog kills a hung worker → this throws
                Disarm();
                if (!ok) { Die(); Silence(output, frames); return; }
                CopyOut(output, 0, frames);
                CopyOut(output, 1, frames);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                Die();
                Silence(output, frames);
            }
        }
    }

    public double GetParameter(int index)
    {
        lock (_lock)
        {
            if (_dead || _disposed) return 0;
            try
            {
                _writer.Write(CmdGetParam); _writer.Write(index); _writer.Flush();
                Arm(ControlTimeoutMs);
                var v = _reader.ReadByte() == RespParamValue ? _reader.ReadDouble() : 0;
                Disarm();
                return v;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException) { Die(); return 0; }
        }
    }

    public void SetParameter(int index, double normalized)
    {
        lock (_lock)
        {
            if (_dead || _disposed) return;
            try
            {
                _writer.Write(CmdSetParam); _writer.Write(index); _writer.Write(normalized); _writer.Flush();
                Arm(ControlTimeoutMs);
                var ack = _reader.ReadByte() == RespAck;
                Disarm();
                if (!ack) Die();
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException) { Die(); }
        }
    }

    private const int StateTimeoutMs = 5_000;   // state blobs can be larger; allow more headroom

    public byte[] SaveState()
    {
        lock (_lock)
        {
            if (_dead || _disposed) return [];
            try
            {
                _writer.Write(CmdSaveState); _writer.Flush();
                Arm(StateTimeoutMs);
                if (_reader.ReadByte() != RespState) { Disarm(); return []; }
                var len = _reader.ReadInt32();
                var blob = len > 0 ? _reader.ReadBytes(len) : [];
                Disarm();
                return blob;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException) { Die(); return []; }
        }
    }

    public void LoadState(ReadOnlySpan<byte> state)
    {
        lock (_lock)
        {
            if (_dead || _disposed) return;
            try
            {
                _writer.Write(CmdLoadState); _writer.Write(state.Length); _writer.Write(state); _writer.Flush();
                Arm(StateTimeoutMs);
                var ack = _reader.ReadByte() == RespAck;
                Disarm();
                if (!ack) Die();
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException) { Die(); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Kill the worker BEFORE taking the lock: if a request is hung holding it, killing unblocks the
        // pipe read so the lock can be acquired. Then stop the watchdog and release everything.
        _watchdogStop = true;
        Die();
        try { _watchdog.Join(1000); } catch { }
        lock (_lock)
        {
            try { _writer.Dispose(); } catch { }
            try { _reader.Dispose(); } catch { }
            try { _toWorker.Dispose(); } catch { }
            try { _fromWorker.Dispose(); } catch { }
            if (_base != null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _base = null; }
            try { _view.Dispose(); } catch { }
            try { _mmf.Dispose(); } catch { }
            try { File.Delete(_mmfPath); } catch { }
            try { _worker.Dispose(); } catch { }
        }
    }

    /// <summary>Test seam: forcibly kill the worker to simulate a plugin crash.</summary>
    internal void KillWorkerForTesting() { try { _worker.Kill(); _worker.WaitForExit(2000); } catch { } }

    private void Die()
    {
        _dead = true;
        Disarm();
        try { if (!_worker.HasExited) _worker.Kill(); } catch { }
    }

    private void CopyIn(PlanarBuffers src, int channel, int frames)
        => new ReadOnlySpan<float>(src.Channel(channel), frames)
            .CopyTo(new Span<float>((float*)(_base + RegionOffset(channel, _maxFrames)), frames));

    private void CopyOut(PlanarBuffers dst, int channel, int frames)
        => new ReadOnlySpan<float>((float*)(_base + RegionOffset(2 + channel, _maxFrames)), frames)
            .CopyTo(new Span<float>(dst.Channel(channel), frames));

    private static void Silence(PlanarBuffers output, int frames)
    {
        for (var c = 0; c < output.ChannelCount; c++)
            new Span<float>(output.Channel(c), frames).Clear();
    }
}
