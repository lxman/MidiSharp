using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.Vst3.Vst3Abi;

namespace MidiSharp.Hosting.Vst3;

/// <summary>
/// The VST3 format adapter: discovers <c>.vst3</c> bundles on the VST3 search path, resolves the binary
/// inside the bundle, calls <c>GetPluginFactory</c>, and enumerates the audio-effect classes. A class id
/// (TUID) is hex-encoded as the descriptor id.
/// </summary>
public sealed unsafe class Vst3Format : IPluginFormat
{
    public string Name => "VST3";

    public IEnumerable<string> DefaultSearchPaths
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("VST3_PATH");
            if (!string.IsNullOrEmpty(env))
            {
                foreach (var p in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    yield return p;
                yield break;
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".vst3");
            yield return "/usr/lib/vst3";
            yield return "/usr/local/lib/vst3";
            var common = Environment.GetEnvironmentVariable("CommonProgramFiles");
            if (!string.IsNullOrEmpty(common)) yield return Path.Combine(common, "VST3");
            yield return Path.Combine(home, "Library", "Audio", "Plug-Ins", "VST3");
        }
    }

    public IEnumerable<string> EnumerateFiles(IEnumerable<string> searchPaths)
    {
        foreach (var dir in searchPaths)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*.vst3").OrderBy(f => f, StringComparer.Ordinal))
                yield return entry;
        }
    }


    /// <summary>Enumerate every file, then scan each (the conventional crash-resilient composition).</summary>
    public IEnumerable<PluginDescriptor> Scan(IEnumerable<string> searchPaths) => EnumerateFiles(searchPaths).SelectMany(ScanFile);

    public IEnumerable<PluginDescriptor> ScanFile(string file)
    {
        var binary = ResolveBinary(file);
        return binary == null ? [] : ScanBinary(binary);
    }

    // A .vst3 is either a single shared library or a bundle directory with the binary under
    // Contents/<arch>-linux/. Pick the platform binary: by VST3 convention the plugin .so is named after
    // the bundle (e.g. lsp-plugins.vst3 → lsp-plugins.so) — prefer that, since a bundle may also ship
    // helper libraries (GL renderers, shared deps) that have no plugin factory.
    internal static string? ResolveBinary(string vst3)
    {
        if (File.Exists(vst3)) return vst3;
        if (!Directory.Exists(vst3)) return null;
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64-linux" : "x86_64-linux";
        var contents = Path.Combine(vst3, "Contents", arch);
        if (!Directory.Exists(contents)) return null;
        var named = Path.Combine(contents, Path.GetFileNameWithoutExtension(vst3) + ".so");
        if (File.Exists(named)) return named;
        return Directory.EnumerateFiles(contents, "*.so").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
    }

    private static IEnumerable<PluginDescriptor> ScanBinary(string binary)
    {
        var results = new List<PluginDescriptor>();
        if (!NativeLibrary.TryLoad(binary, out var lib)) return results;
        try
        {
            var factory = GetFactory(lib);
            if (factory == null) return results;
            var v = (FactoryVtbl*)*(void**)factory;
            // IPluginFactory2 (if supported) exposes subCategories — that's where "Instrument" lives.
            void* factory2 = null;
            fixed (byte* iid2 = IidPluginFactory2)
                if (!Ok(((delegate* unmanaged[Cdecl]<void*, byte*, void**, int>)(*(IntPtr**)factory)[0])(factory, iid2, &factory2)))
                    factory2 = null;
            var f2 = factory2 != null ? (Factory2Vtbl*)*(void**)factory2 : null;

            var count = v->CountClasses(factory);
            for (var i = 0; i < count; i++)
            {
                PClassInfo info;
                if (!Ok(v->GetClassInfo(factory, i, &info))) continue;
                if (Ascii(info.Category, 32) != AudioModuleCategory) continue;
                var cid = new byte[16];
                for (var b = 0; b < 16; b++) cid[b] = info.Cid[b];

                var isInstrument = false;
                if (f2 != null)
                {
                    PClassInfo2 info2;
                    if (Ok(f2->GetClassInfo2(factory2, i, &info2)))
                        isInstrument = Ascii(info2.SubCategories, 128).Contains("Instrument", StringComparison.OrdinalIgnoreCase);
                }

                results.Add(new PluginDescriptor(
                    Format: "VST3",
                    Id: Convert.ToHexString(cid),
                    Name: Ascii(info.Name, 64),
                    Vendor: "",
                    IsInstrument: isInstrument,
                    Path: binary));
            }
            if (factory2 != null) ((delegate* unmanaged[Cdecl]<void*, uint>)(*(IntPtr**)factory2)[2])(factory2);
        }
        finally { NativeLibrary.Free(lib); }
        return results;
    }

    public IHostedPlugin Load(PluginDescriptor descriptor, AudioConfig config)
    {
        if (descriptor.Format != Name)
            throw new ArgumentException($"Not a VST3 descriptor: {descriptor.Format}", nameof(descriptor));

        var lib = NativeLibrary.Load(descriptor.Path);
        try
        {
            var factory = GetFactory(lib);
            if (factory == null) throw new InvalidOperationException($"'{descriptor.Path}' has no usable VST3 factory.");

            var cid = Convert.FromHexString(descriptor.Id);
            void* component = null;
            var v = (FactoryVtbl*)*(void**)factory;
            fixed (byte* cidp = cid)
            fixed (byte* iidp = IidComponent)
                if (!Ok(v->CreateInstance(factory, cidp, iidp, &component)) || component == null)
                    throw new InvalidOperationException($"createInstance failed for VST3 {descriptor.Name}.");

            // The factory stays valid while the library is loaded — the plugin keeps it to create a separate
            // edit controller class if the component names one.
            return new Vst3Plugin(lib, factory, component, descriptor, config);   // wrapper owns lib + releases component
        }
        catch
        {
            NativeLibrary.Free(lib);
            throw;
        }
    }

    // GetPluginFactory(), after ModuleEntry() if the binary exports it.
    private static void* GetFactory(IntPtr lib)
    {
        if (NativeLibrary.TryGetExport(lib, "ModuleEntry", out var entry))
            ((delegate* unmanaged[Cdecl]<void*, byte>)entry)(null);
        if (!NativeLibrary.TryGetExport(lib, "GetPluginFactory", out var getFactory))
            return null;
        return ((delegate* unmanaged[Cdecl]<void*>)getFactory)();
    }
}
