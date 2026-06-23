using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

        // AU v3 components are delivered by Apple's bridge and must be instantiated asynchronously (and the
        // effects out-of-process); a legacy v2 AU keeps the synchronous path. Either way the resulting
        // AudioComponentInstance is driven by the same AudioUnitPlugin — the v2 C API works over the v3 bridge
        // (Plan A Task 0 spike). See AudioUnitAbi for the AudioComponentFlags meanings.
        IntPtr au = RequiresAsyncLoad(comp, out bool inProcess)
            ? InstantiateAsync(comp, inProcess ? InstantiationLoadInProcess : InstantiationLoadOutOfProcess, descriptor.Id)
            : InstantiateSync(comp, descriptor.Id);
        return new AudioUnitPlugin(au, descriptor, config);
    }

    private static IntPtr InstantiateSync(IntPtr comp, string id)
    {
        int st = AudioComponentInstanceNew(comp, out IntPtr au);
        if (st != 0 || au == IntPtr.Zero)
            throw new InvalidOperationException($"AudioComponentInstanceNew failed ({st}) for '{id}'.");
        return au;
    }

    /// <summary>True when a component must take the async <c>AudioComponentInstantiate</c> path: any AU that
    /// requires async, plus a v3 AU that can't load in-process. A legacy v2 AU (neither bit set) stays
    /// synchronous. <paramref name="inProcess"/> is the placement to request when async.</summary>
    private static bool RequiresAsyncLoad(IntPtr comp, out bool inProcess)
    {
        inProcess = true;
        AudioComponentDescription d;
        if (AudioComponentGetDescription(comp, &d) != 0) return false;   // can't read flags → treat as legacy/sync
        bool isV3 = (d.ComponentFlags & CompFlagIsV3AudioUnit) != 0;
        bool requiresAsync = (d.ComponentFlags & CompFlagRequiresAsync) != 0;
        inProcess = (d.ComponentFlags & CompFlagCanLoadInProcess) != 0;
        return requiresAsync || (isV3 && !inProcess);
    }

    // ── async (AU v3) instantiation ──
    // AudioComponentInstantiate delivers its result on the run loop via an Obj-C completion block. We use one
    // process-global block; a gate serializes the (rare, and worker-thread-single) loads so the static handshake
    // fields are unambiguous.
    private static readonly object s_asyncGate = new();
    private static IntPtr s_asyncInstance;
    private static int s_asyncStatus;
    private static volatile bool s_asyncDone;
    private static readonly IntPtr s_completionBlock =
        AuBlocks.MakeGlobalBlock((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, void>)&AsyncInstantiateCompleted);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void AsyncInstantiateCompleted(IntPtr block, IntPtr instance, int status)
    {
        s_asyncInstance = instance;
        s_asyncStatus = status;
        s_asyncDone = true;
    }

    private static IntPtr InstantiateAsync(IntPtr comp, uint options, string id)
    {
        lock (s_asyncGate)
        {
            s_asyncInstance = IntPtr.Zero;
            s_asyncStatus = 0;
            s_asyncDone = false;
            AudioComponentInstantiate(comp, options, s_completionBlock);
            // The completion fires on this thread's run loop — pump it (≤ ~20 s) until the handshake flips.
            for (int i = 0; i < 400 && !s_asyncDone; i++)
                CoreFoundation.PumpRunLoop(0.05);
            if (!s_asyncDone)
                throw new InvalidOperationException($"Async instantiation of '{id}' timed out.");
            if (s_asyncStatus != 0 || s_asyncInstance == IntPtr.Zero)
                throw new InvalidOperationException($"AudioComponentInstantiate failed ({s_asyncStatus}) for '{id}'.");
            return s_asyncInstance;
        }
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
