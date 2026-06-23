using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static MidiSharp.Hosting.AudioUnit.AudioUnitAbi;

namespace MidiSharp.Hosting.AudioUnit;

/// <summary>
/// The Audio Unit (AU v2) format adapter (macOS-only). Discovery is registry-based: <c>AudioComponentFindNext</c>
/// enumerates every installed AU (Apple system units like <c>AULowpass</c>/<c>DLSMusicDevice</c> and third-party
/// <c>.component</c>s alike) without instantiating any of them, so it is inherently crash-safe. The
/// <see cref="EnumerateFiles"/>/<see cref="ScanFile"/> pair additionally walks <c>.component</c> bundles on disk
/// and reads their <c>Info.plist</c> (no native code), to honor the per-file scan contract.
/// </summary>
/// <remarks>
/// A plugin's identity travels in <see cref="PluginDescriptor.Id"/> as its <c>type:subtype:manufacturer</c>
/// OSType triple (e.g. <c>"aufx:lpas:appl"</c>), which <see cref="Load"/> parses back to resolve the live
/// <c>AudioComponent</c>.
/// </remarks>
public sealed unsafe class AudioUnitFormat : IPluginFormat
{
    public string Name => "AU";

    public IEnumerable<string> DefaultSearchPaths
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, "Library", "Audio", "Plug-Ins", "Components");   // per-user
            yield return "/Library/Audio/Plug-Ins/Components";                                // system (third-party)
            yield return "/System/Library/Components";                                        // Apple-shipped
        }
    }

    public IEnumerable<string> EnumerateFiles(IEnumerable<string> searchPaths)
    {
        foreach (string dir in searchPaths)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir, "*.component").OrderBy(f => f, StringComparer.Ordinal))
                yield return entry;
        }
    }

    /// <summary>The registry (every installed AU) unioned with the on-disk bundle scan, de-duplicated by the
    /// type:subtype:manufacturer triple. The registry already covers installed third-party AUs; the disk pass
    /// is the per-file contract and a backstop.</summary>
    public IEnumerable<PluginDescriptor> Scan(IEnumerable<string> searchPaths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (PluginDescriptor d in ScanRegistry())
            if (seen.Add(d.Id)) yield return d;
        foreach (string file in EnumerateFiles(searchPaths))
            foreach (PluginDescriptor d in ScanFile(file))
                if (seen.Add(d.Id)) yield return d;
    }

    /// <summary>Read one <c>.component</c> bundle's <c>Info.plist</c> <c>AudioComponents</c> array via
    /// CoreFoundation — no plugin code loaded.</summary>
    public IEnumerable<PluginDescriptor> ScanFile(string file)
    {
        var results = new List<PluginDescriptor>();
        string info = Path.Combine(file, "Contents", "Info.plist");
        if (!File.Exists(info)) return results;

        IntPtr plist = CoreFoundation.CreatePropertyList(File.ReadAllBytes(info));
        if (plist == IntPtr.Zero) return results;
        try
        {
            IntPtr arr = CoreFoundation.DictGet(plist, "AudioComponents");
            nint n = CoreFoundation.ArrayCount(arr);
            for (nint i = 0; i < n; i++)
            {
                IntPtr entry = CoreFoundation.ArrayGet(arr, i);
                string type = CoreFoundation.DictGetString(entry, "type");
                string sub = CoreFoundation.DictGetString(entry, "subtype");
                string manu = CoreFoundation.DictGetString(entry, "manufacturer");
                if (type.Length != 4 || sub.Length != 4 || manu.Length != 4) continue;
                uint t = FourCC(type);
                if (!IsHostable(t)) continue;
                results.Add(Describe(t, FourCC(sub), FourCC(manu), CoreFoundation.DictGetString(entry, "name"), file));
            }
        }
        finally { CoreFoundation.CFRelease(plist); }
        return results;
    }

    public IHostedPlugin Load(PluginDescriptor descriptor, AudioConfig config)
    {
        if (descriptor.Format != Name)
            throw new ArgumentException($"Not an AU descriptor: {descriptor.Format}", nameof(descriptor));
        if (!TryParseId(descriptor.Id, out uint type, out uint sub, out uint manu))
            throw new InvalidOperationException($"Malformed AU id '{descriptor.Id}'.");

        var desc = new AudioComponentDescription { ComponentType = type, ComponentSubType = sub, ComponentManufacturer = manu };
        IntPtr comp = AudioComponentFindNext(IntPtr.Zero, &desc);
        if (comp == IntPtr.Zero)
            throw new InvalidOperationException($"No installed Audio Unit matches '{descriptor.Id}'.");

        int st = AudioComponentInstanceNew(comp, out IntPtr au);
        if (st != 0 || au == IntPtr.Zero)
            throw new InvalidOperationException($"AudioComponentInstanceNew failed ({st}) for '{descriptor.Id}'.");
        return new AudioUnitPlugin(au, descriptor, config);
    }

    // ── discovery internals ──
    private List<PluginDescriptor> ScanRegistry()
    {
        var results = new List<PluginDescriptor>();
        var any = default(AudioComponentDescription);   // all-zero == wildcard: match every component
        IntPtr comp = IntPtr.Zero;
        while ((comp = AudioComponentFindNext(comp, &any)) != IntPtr.Zero)
        {
            AudioComponentDescription d;
            if (AudioComponentGetDescription(comp, &d) != 0) continue;
            if (!IsHostable(d.ComponentType)) continue;

            string full = "";
            if (AudioComponentCopyName(comp, out IntPtr cfName) == 0 && cfName != IntPtr.Zero)
            {
                full = CoreFoundation.ToManaged(cfName);
                CoreFoundation.CFRelease(cfName);
            }
            results.Add(Describe(d.ComponentType, d.ComponentSubType, d.ComponentManufacturer, full, ""));
        }
        return results;
    }

    private static bool IsHostable(uint type) =>
        type == TypeEffect || type == TypeMusicEffect || type == TypeMusicDevice || type == TypeGenerator;

    private static PluginDescriptor Describe(uint type, uint sub, uint manu, string fullName, string path)
    {
        // AudioComponentCopyName is conventionally "Manufacturer: Name".
        int split = fullName.IndexOf(": ", StringComparison.Ordinal);
        string vendor = split > 0 ? fullName[..split] : "";
        string name = split > 0 ? fullName[(split + 2)..] : fullName;
        if (name.Length == 0) name = Str(sub).Trim();
        return new PluginDescriptor("AU", $"{Str(type)}:{Str(sub)}:{Str(manu)}", name, vendor, IsInstrumentType(type), path);
    }

    private static bool TryParseId(string id, out uint type, out uint sub, out uint manu)
    {
        type = sub = manu = 0;
        string[] parts = id.Split(':');
        if (parts.Length != 3 || parts[0].Length != 4 || parts[1].Length != 4 || parts[2].Length != 4) return false;
        type = FourCC(parts[0]); sub = FourCC(parts[1]); manu = FourCC(parts[2]);
        return true;
    }

    /// <summary>An OSType back to its four characters (e.g. <c>'aufx'</c>).</summary>
    private static string Str(uint c) => new([(char)((c >> 24) & 0xFF), (char)((c >> 16) & 0xFF), (char)((c >> 8) & 0xFF), (char)(c & 0xFF)]);
}
