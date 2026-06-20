using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using SysProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using static MidiSharp.Hosting.Sandbox.SandboxProtocol;

namespace MidiSharp.Hosting.Sandbox;

/// <summary>
/// Discovers plugins out-of-process: one worker per format scans and streams its descriptors back as it
/// finds them. A plugin that crashes the scan kills only that format's worker — the host keeps the
/// descriptors already streamed (a partial result for that format) and the other formats are untouched,
/// so the server's startup scan can never be brought down by a bad plugin.
/// </summary>
public static class SandboxScanner
{
    /// <summary>Scan every format in its own worker; concatenate the results (partial on a worker crash).</summary>
    public static List<PluginDescriptor> ScanAll(IEnumerable<string> formats, string workerDll)
    {
        var all = new List<PluginDescriptor>();
        foreach (var format in formats)
            all.AddRange(ScanFormat(format, workerDll));
        return all;
    }

    /// <summary>Scan one format in a worker, returning whatever it streamed before finishing or dying.</summary>
    public static List<PluginDescriptor> ScanFormat(string format, string workerDll)
    {
        var found = new List<PluginDescriptor>();
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        psi.ArgumentList.Add(workerDll);
        psi.ArgumentList.Add("--scan");
        psi.ArgumentList.Add(format);
        psi.ArgumentList.Add(pipe.GetClientHandleAsString());

        SysProcess? worker = null;
        try
        {
            worker = SysProcess.Start(psi);
            if (worker == null) return found;
            pipe.DisposeLocalCopyOfClientHandle();

            using var reader = new BinaryReader(pipe);
            while (true)
            {
                byte tag;
                try { tag = reader.ReadByte(); }
                catch (EndOfStreamException) { break; }   // worker finished or crashed mid-scan

                if (tag == ScanDone) break;
                if (tag != ScanDescriptor) break;
                found.Add(new PluginDescriptor(
                    reader.ReadString(), reader.ReadString(), reader.ReadString(),
                    reader.ReadString(), reader.ReadBoolean(), reader.ReadString()));
            }
        }
        catch { /* the scan worker failing is non-fatal — keep partials */ }
        finally
        {
            try { if (worker is { HasExited: false }) { worker.WaitForExit(1000); if (!worker.HasExited) worker.Kill(); } } catch { }
            worker?.Dispose();
        }
        return found;
    }
}
