using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
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
    private bool _dead;
    private bool _disposed;
    private readonly object _lock = new();

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

        // Read the worker's ready message (or an error).
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
        catch (EndOfStreamException) { throw new InvalidOperationException("Sandbox worker exited during startup."); }
    }

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
                if (_reader.ReadByte() != RespProcessed) { Die(); Silence(output, frames); return; }
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
                return _reader.ReadByte() == RespParamValue ? _reader.ReadDouble() : 0;
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
                if (_reader.ReadByte() != RespAck) Die();
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException) { Die(); }
        }
    }

    public byte[] SaveState() => [];                       // state proxying is a follow-up
    public void LoadState(ReadOnlySpan<byte> state) { }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_dead)
                try { _writer.Write(CmdDispose); _writer.Flush(); } catch { }
            try { if (!_worker.WaitForExit(1000)) _worker.Kill(); } catch { }
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
