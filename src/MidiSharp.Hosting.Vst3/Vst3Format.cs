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
    // Contents/<arch>-linux/. Pick the platform binary.
    internal static string? ResolveBinary(string vst3)
    {
        if (File.Exists(vst3)) return vst3;
        if (!Directory.Exists(vst3)) return null;
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64-linux" : "x86_64-linux";
        var contents = Path.Combine(vst3, "Contents", arch);
        if (!Directory.Exists(contents)) return null;
        return Directory.EnumerateFiles(contents, "*.so").FirstOrDefault();
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
            var count = v->CountClasses(factory);
            for (var i = 0; i < count; i++)
            {
                PClassInfo info;
                if (!Ok(v->GetClassInfo(factory, i, &info))) continue;
                if (Ascii(info.Category, 32) != AudioModuleCategory) continue;
                var cid = new byte[16];
                for (var b = 0; b < 16; b++) cid[b] = info.Cid[b];
                results.Add(new PluginDescriptor(
                    Format: "VST3",
                    Id: Convert.ToHexString(cid),
                    Name: Ascii(info.Name, 64),
                    Vendor: "",
                    IsInstrument: false,   // refined from subcategories later; effects for now
                    Path: binary));
            }
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

            return new Vst3Plugin(lib, component, descriptor, config);   // wrapper owns lib + releases component
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
