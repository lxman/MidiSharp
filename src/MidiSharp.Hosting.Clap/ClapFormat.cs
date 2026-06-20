using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.Clap.ClapAbi;

namespace MidiSharp.Hosting.Clap;

/// <summary>
/// The CLAP format adapter: discovers <c>.clap</c> libraries on the CLAP search path and loads one by
/// its plugin id. CLAP is cross-platform (a <c>.clap</c> is a plain shared library exporting the
/// <c>clap_entry</c> symbol), so <see cref="System.Runtime.InteropServices.NativeLibrary"/> loads it on
/// Linux/Windows/macOS alike.
/// </summary>
public sealed unsafe class ClapFormat : IPluginFormat
{
    public string Name => "CLAP";

    public IEnumerable<string> DefaultSearchPaths
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CLAP_PATH");
            if (!string.IsNullOrEmpty(env))
            {
                foreach (var p in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    yield return p;
                yield break;
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // Linux/user; the Windows/macOS standard dirs slot in here as cross-platform support grows.
            yield return Path.Combine(home, ".clap");
            yield return "/usr/lib/clap";
            yield return "/usr/local/lib/clap";
            var common = Environment.GetEnvironmentVariable("CommonProgramFiles");
            if (!string.IsNullOrEmpty(common)) yield return Path.Combine(common, "CLAP");
            yield return Path.Combine(home, "Library", "Audio", "Plug-Ins", "CLAP");   // macOS user
        }
    }

    public IEnumerable<PluginDescriptor> Scan(IEnumerable<string> searchPaths)
    {
        foreach (var dir in searchPaths)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.clap"))
                foreach (var d in ScanFile(file))
                    yield return d;
        }
    }

    private static IEnumerable<PluginDescriptor> ScanFile(string file)
    {
        var results = new List<PluginDescriptor>();
        if (!NativeLibrary.TryLoad(file, out var lib)) return results;
        try
        {
            if (!NativeLibrary.TryGetExport(lib, EntryExport, out var entryPtr)) return results;
            var entry = (ClapPluginEntry*)entryPtr;
            var path = Utf8(file);
            try
            {
                if (entry->Init((byte*)path) == 0) return results;
                var factory = (ClapPluginFactory*)entry->GetFactory(Utf8Span(FactoryId));
                if (factory == null) return results;

                var count = factory->GetPluginCount(factory);
                for (uint i = 0; i < count; i++)
                {
                    var desc = factory->GetPluginDescriptor(factory, i);
                    if (desc == null) continue;
                    results.Add(new PluginDescriptor(
                        Format: "CLAP",
                        Id: Str(desc->Id),
                        Name: Str(desc->Name),
                        Vendor: Str(desc->Vendor),
                        IsInstrument: HasFeature(desc, "instrument"),
                        Path: file));
                }
            }
            finally { Marshal.FreeHGlobal(path); }
        }
        finally { NativeLibrary.Free(lib); }
        return results;
    }

    public IHostedPlugin Load(PluginDescriptor descriptor, AudioConfig config)
    {
        if (descriptor.Format != Name)
            throw new ArgumentException($"Not a CLAP descriptor: {descriptor.Format}", nameof(descriptor));

        var lib = NativeLibrary.Load(descriptor.Path);
        ClapHost? host = null;
        try
        {
            if (!NativeLibrary.TryGetExport(lib, EntryExport, out var entryPtr))
                throw new InvalidOperationException($"'{descriptor.Path}' has no clap_entry symbol.");
            var entry = (ClapPluginEntry*)entryPtr;

            var path = Utf8(descriptor.Path);
            try
            {
                if (entry->Init((byte*)path) == 0)
                    throw new InvalidOperationException($"clap_entry.init failed for {descriptor.Path}.");
            }
            finally { Marshal.FreeHGlobal(path); }

            var factory = (ClapPluginFactory*)entry->GetFactory(Utf8Span(FactoryId));
            if (factory == null) throw new InvalidOperationException("CLAP plugin factory unavailable.");

            host = new ClapHost();
            var idPtr = Utf8(descriptor.Id);
            try
            {
                var plugin = factory->CreatePlugin(factory, host.Pointer, (byte*)idPtr);
                if (plugin == null) throw new InvalidOperationException($"create_plugin failed for {descriptor.Id}.");
                if (plugin->Init(plugin) == 0)
                {
                    plugin->Destroy(plugin);
                    throw new InvalidOperationException($"plugin.init failed for {descriptor.Id}.");
                }
                return new ClapPlugin(lib, entry, host, plugin, descriptor, config);   // wrapper owns lib + host
            }
            finally { Marshal.FreeHGlobal(idPtr); }
        }
        catch
        {
            host?.Dispose();
            NativeLibrary.Free(lib);
            throw;
        }
    }

    private static bool HasFeature(ClapPluginDescriptor* desc, string feature)
    {
        if (desc->Features == null) return false;
        for (var i = 0; ; i++)
        {
            var f = desc->Features[i];
            if (f == null) return false;
            if (Str(f) == feature) return true;
        }
    }

    // Small UTF-8 helpers. Utf8 allocates (caller frees); Utf8Span uses a pinned cache for short, constant ids.
    private static IntPtr Utf8(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var p = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        ((byte*)p)[bytes.Length] = 0;
        return p;
    }

    private static readonly Dictionary<string, IntPtr> ConstUtf8 = new();
    private static byte* Utf8Span(string s)
    {
        lock (ConstUtf8)
        {
            if (!ConstUtf8.TryGetValue(s, out var p)) { p = Utf8(s); ConstUtf8[s] = p; }
            return (byte*)p;
        }
    }
}
