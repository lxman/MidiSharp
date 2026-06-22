using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using SysProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using static MidiSharp.Hosting.Sandbox.SandboxProtocol;

namespace MidiSharp.Hosting.Sandbox;

/// <summary>
/// Discovers plugins out-of-process: one worker per format scans file-by-file and streams its descriptors
/// back. The worker announces each file before touching it, so if a plugin crashes the scan, the host
/// knows exactly which file died and restarts the worker to resume PAST it — skipping only the one bad
/// plugin. The host keeps everything streamed so far; other formats are untouched. The server's startup
/// scan can therefore never be brought down (or significantly diminished) by a bad plugin.
/// </summary>
public static class SandboxScanner
{
    /// <summary>Scan every format in its own resumable worker; concatenate the results.</summary>
    public static List<PluginDescriptor> ScanAll(IEnumerable<string> formats, string workerDll)
    {
        var all = new List<PluginDescriptor>();
        foreach (string format in formats)
            all.AddRange(ScanFormat(format, workerDll));
        return all;
    }

    /// <summary>
    /// Scan one format, resuming a fresh worker past any file that crashes the scan, until the scan
    /// completes (or no further progress is possible). <paramref name="searchPaths"/> overrides the
    /// format's default directories when given (used by tests).
    /// </summary>
    public static List<PluginDescriptor> ScanFormat(string format, string workerDll, IEnumerable<string>? searchPaths = null)
    {
        var found = new List<PluginDescriptor>();
        string[] paths = searchPaths?.ToArray() ?? [];
        var resumeAfter = "";
        var guard = 0;   // bound the resume loop: at most one restart per crashing file

        while (guard++ < 10_000)
        {
            (bool done, string? lastFile) = RunOnce(format, workerDll, resumeAfter, paths, found);
            if (done) break;                       // clean finish (ScanDone)
            if (lastFile == null || lastFile == resumeAfter) break;   // crashed before any file, or no progress
            resumeAfter = lastFile;                // crashed scanning lastFile → resume past it
        }
        return found;
    }

    // One worker run. Returns (reachedDone, lastFileAnnounced). Appends streamed descriptors to `found`.
    private static (bool done, string? lastFile) RunOnce(
        string format, string workerDll, string resumeAfter, string[] paths, List<PluginDescriptor> found)
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        psi.ArgumentList.Add(workerDll);
        psi.ArgumentList.Add("--scan");
        psi.ArgumentList.Add(format);
        psi.ArgumentList.Add(pipe.GetClientHandleAsString());
        psi.ArgumentList.Add(resumeAfter);
        foreach (string p in paths) psi.ArgumentList.Add(p);

        SysProcess? worker = null;
        string? lastFile = null;
        var done = false;
        try
        {
            worker = SysProcess.Start(psi);
            if (worker == null) return (true, null);   // can't start → give up cleanly
            pipe.DisposeLocalCopyOfClientHandle();

            using var reader = new BinaryReader(pipe);
            while (true)
            {
                byte tag;
                try { tag = reader.ReadByte(); }
                catch (EndOfStreamException) { break; }   // worker finished or crashed

                if (tag == ScanDone) { done = true; break; }
                if (tag == ScanBegin) { lastFile = reader.ReadString(); continue; }
                if (tag == ScanDescriptor)
                {
                    found.Add(new PluginDescriptor(
                        reader.ReadString(), reader.ReadString(), reader.ReadString(),
                        reader.ReadString(), reader.ReadBoolean(), reader.ReadString()));
                    continue;
                }
                break;
            }
        }
        catch { /* worker failure is non-fatal — keep partials, treat as crash */ }
        finally
        {
            try { if (worker is { HasExited: false }) { worker.WaitForExit(1000); if (!worker.HasExited) worker.Kill(); } } catch { }
            worker?.Dispose();
        }
        return (done, lastFile);
    }
}
