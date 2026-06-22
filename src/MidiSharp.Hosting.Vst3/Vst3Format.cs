using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
            string? env = Environment.GetEnvironmentVariable("VST3_PATH");
            if (!string.IsNullOrEmpty(env))
            {
                foreach (string p in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    yield return p;
                yield break;
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".vst3");                                   // Linux user
            yield return "/usr/lib/vst3";
            yield return "/usr/local/lib/vst3";
            string? common = Environment.GetEnvironmentVariable("CommonProgramFiles");
            if (!string.IsNullOrEmpty(common)) yield return Path.Combine(common, "VST3");   // Windows system
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData)) yield return Path.Combine(localAppData, "Programs", "Common", "VST3");   // Windows user
            yield return Path.Combine(home, "Library", "Audio", "Plug-Ins", "VST3");    // macOS user
        }
    }

    public IEnumerable<string> EnumerateFiles(IEnumerable<string> searchPaths)
    {
        foreach (string dir in searchPaths)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir, "*.vst3").OrderBy(f => f, StringComparer.Ordinal))
                yield return entry;
        }
    }


    /// <summary>Enumerate every file, then scan each (the conventional crash-resilient composition).</summary>
    public IEnumerable<PluginDescriptor> Scan(IEnumerable<string> searchPaths) => EnumerateFiles(searchPaths).SelectMany(ScanFile);

    public IEnumerable<PluginDescriptor> ScanFile(string file)
    {
        string? binary = ResolveBinary(file);
        return binary == null ? [] : ScanBinary(binary);
    }

    // A .vst3 is either a single shared library (a bare file — common on Windows) or a bundle directory with
    // the binary under Contents/<platform>/. The platform subfolder and binary extension differ by OS:
    //   Linux   → Contents/{x86_64,aarch64}-linux/<name>.so
    //   Windows → Contents/{x86_64,arm64}-win/<name>.vst3   (a DLL named .vst3)
    //   macOS   → Contents/MacOS/<name>            (no extension)
    // By VST3 convention the binary is named after the bundle, so prefer that — a bundle may also ship
    // helper libraries (GL renderers, shared deps) that have no plugin factory.
    internal static string? ResolveBinary(string vst3)
    {
        if (File.Exists(vst3)) return vst3;
        if (!Directory.Exists(vst3)) return null;
        string name = Path.GetFileNameWithoutExtension(vst3);

        string subdir, named;
        string[] patterns;
        bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
        {
            subdir = arm ? "arm64-win" : "x86_64-win";
            named = name + ".vst3";
            patterns = ["*.vst3", "*.dll"];
        }
        else if (OperatingSystem.IsMacOS())
        {
            subdir = "MacOS";
            named = name;            // macOS bundle binaries have no extension
            patterns = ["*"];
        }
        else
        {
            subdir = arm ? "aarch64-linux" : "x86_64-linux";
            named = name + ".so";
            patterns = ["*.so"];
        }

        string contents = Path.Combine(vst3, "Contents", subdir);
        if (!Directory.Exists(contents)) return null;
        string namedPath = Path.Combine(contents, named);
        if (File.Exists(namedPath)) return namedPath;
        foreach (string p in patterns)
        {
            string? f = Directory.EnumerateFiles(contents, p).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
            if (f != null) return f;
        }
        return null;
    }

    private static IEnumerable<PluginDescriptor> ScanBinary(string binary)
    {
        var results = new List<PluginDescriptor>();
        if (!NativeLibrary.TryLoad(binary, out IntPtr lib)) return results;
        try
        {
            void* factory = GetFactory(lib);
            if (factory == null) return results;
            var v = (FactoryVtbl*)*(void**)factory;
            // IPluginFactory2 (if supported) exposes subCategories — that's where "Instrument" lives.
            void* factory2 = null;
            fixed (byte* iid2 = IidPluginFactory2)
                if (!Ok(((delegate* unmanaged[Cdecl]<void*, byte*, void**, int>)(*(IntPtr**)factory)[0])(factory, iid2, &factory2)))
                    factory2 = null;
            Factory2Vtbl* f2 = factory2 != null ? (Factory2Vtbl*)*(void**)factory2 : null;

            int count = v->CountClasses(factory);
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

        IntPtr lib = NativeLibrary.Load(descriptor.Path);
        try
        {
            void* factory = GetFactory(lib);
            if (factory == null) throw new InvalidOperationException($"'{descriptor.Path}' has no usable VST3 factory.");

            byte[] cid = Convert.FromHexString(descriptor.Id);
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
        if (NativeLibrary.TryGetExport(lib, "ModuleEntry", out IntPtr entry))
            ((delegate* unmanaged[Cdecl]<void*, byte>)entry)(null);
        if (!NativeLibrary.TryGetExport(lib, "GetPluginFactory", out IntPtr getFactory))
            return null;
        return ((delegate* unmanaged[Cdecl]<void*>)getFactory)();
    }
}
