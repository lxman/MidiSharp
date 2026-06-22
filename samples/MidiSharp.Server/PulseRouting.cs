using System.Diagnostics;
using MidiSharp.Synth.OwnAudio;

namespace MidiSharp.Server;

/// <summary>
/// Linux PulseAudio/PipeWire stream routing via <c>pactl</c>. On these systems MidiSharp's
/// audio (OwnAudio → ALSA "default") shows up as an ordinary server sink-input, so we can list
/// the real output <em>sinks</em> (friendly names) and route playback to any of them by moving
/// our own stream — independent of OwnAudio's device API and immune to the system default
/// changing under us. On bare ALSA (no sound server) <see cref="IsAvailable"/> is false and the
/// caller falls back to OwnAudio's native device enumeration/selection.
/// </summary>
internal static class PulseRouting
{
    private static bool? s_available;

    /// <summary>True when a PulseAudio/PipeWire server is reachable via pactl.</summary>
    public static bool IsAvailable()
    {
        if (s_available is { } cached) return cached;
        // pactl is a Linux sound-server tool; on Windows/macOS short-circuit so we don't spawn a
        // doomed process just to catch its failure. Elsewhere, probe by actually running it.
        bool ok = OperatingSystem.IsLinux();
        if (ok)
        {
            try { ok = Run("info", out _, timeoutMs: 2000); }
            catch { ok = false; }
        }
        s_available = ok;
        return ok;
    }

    /// <summary>The available output sinks: Id = sink name (stable, used for routing), Name = human description.</summary>
    public static IReadOnlyList<DeviceDto> GetSinks()
    {
        var result = new List<DeviceDto>();
        string defaultSink = "";
        if (Run("get-default-sink", out var def)) defaultSink = def.Trim();

        if (!Run("list sinks", out var listing)) return result;

        // Parse the verbose block listing: each sink starts at "Sink #", with "Name:" and
        // "Description:" lines. Name is the routing key; Description is what the user sees.
        string? name = null, desc = null;
        void Flush()
        {
            if (name is not null)
                result.Add(new DeviceDto(name, desc ?? name, "PulseAudio/PipeWire", name == defaultSink));
            name = desc = null;
        }
        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Sink #", StringComparison.Ordinal)) { Flush(); }
            else if (line.StartsWith("Name:", StringComparison.Ordinal)) name = line["Name:".Length..].Trim();
            else if (line.StartsWith("Description:", StringComparison.Ordinal)) desc = line["Description:".Length..].Trim();
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Route our own playback stream onto <paramref name="sinkName"/>. We identify the stream by the
    /// stable node.name OwnAudio gives it on Linux (<see cref="OwnAudioOutput.LinuxPipeWireNodeName"/>),
    /// picking the highest object.serial so a not-yet-reaped zombie from a previous play can't win.
    /// The stream appears shortly after the engine starts, so we poll briefly. Best-effort: a false
    /// result just leaves playback on the default sink. (module-stream-restore then remembers this
    /// choice for our app, so we're working with it, not against it.)
    /// </summary>
    public static bool MoveOurStreamToSink(string sinkName)
    {
        if (string.IsNullOrWhiteSpace(sinkName)) return false;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var idx = FindOurSinkInput();
            if (idx is not null && Run($"move-sink-input {idx} {sinkName}", out _)) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    // The sink-input index of our own stream: the one whose node.name equals OwnAudio's Linux node
    // name, with the largest object.serial (newest). Returns null if not present yet.
    private static string? FindOurSinkInput()
    {
        if (!Run("list sink-inputs", out var listing)) return null;
        string? curIndex = null;
        bool curIsOurs = false;
        string? bestIndex = null;
        long bestSerial = -1;
        string nodeNeedle = $"node.name = \"{OwnAudioOutput.LinuxPipeWireNodeName}\"";

        void Consider(string? index, bool isOurs, long serial)
        {
            if (index is null || !isOurs) return;
            if (serial >= bestSerial) { bestSerial = serial; bestIndex = index; }
        }

        long curSerial = -1;
        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Sink Input #", StringComparison.Ordinal))
            {
                Consider(curIndex, curIsOurs, curSerial);
                curIndex = line["Sink Input #".Length..].Trim();
                curIsOurs = false;
                curSerial = -1;
            }
            else if (curIndex is not null && line.Contains(nodeNeedle, StringComparison.Ordinal))
                curIsOurs = true;
            else if (curIndex is not null && line.StartsWith("object.serial = \"", StringComparison.Ordinal))
            {
                var s = line["object.serial = \"".Length..].TrimEnd('"');
                if (long.TryParse(s, out var v)) curSerial = v;
            }
        }
        Consider(curIndex, curIsOurs, curSerial);   // last block
        return bestIndex;
    }

    private static bool Run(string args, out string stdout, int timeoutMs = 4000)
    {
        stdout = "";
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("pactl", args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (!p.Start()) return false;
            stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;   // pactl absent → not a Pulse/PipeWire system
        }
    }
}
