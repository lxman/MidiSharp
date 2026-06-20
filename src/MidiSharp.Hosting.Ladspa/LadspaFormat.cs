using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;

namespace MidiSharp.Hosting.Ladspa;

/// <summary>
/// The LADSPA format adapter: discovers <c>.so</c> plugins under the LADSPA search path and loads one
/// by its (globally unique) LADSPA UniqueID. Linux-only.
/// </summary>
/// <remarks>
/// Pending live verification against a real plugin (none installed at scaffold time) — install e.g.
/// <c>swh-plugins</c> / <c>cmt</c> into <c>/usr/lib/ladspa</c> to exercise it. The ABI is transcribed in
/// <see cref="LadspaInterop"/>.
/// </remarks>
public sealed class LadspaFormat : IPluginFormat
{
    public string Name => "LADSPA";

    public IEnumerable<string> DefaultSearchPaths
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("LADSPA_PATH");
            if (!string.IsNullOrEmpty(env))
            {
                foreach (var p in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    yield return p;
                yield break;
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return "/usr/lib/ladspa";
            yield return "/usr/local/lib/ladspa";
            yield return Path.Combine(home, ".ladspa");
        }
    }

    public IEnumerable<string> EnumerateFiles(IEnumerable<string> searchPaths)
    {
        foreach (var dir in searchPaths)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.so").OrderBy(f => f, StringComparer.Ordinal))
                yield return file;
        }
    }


    /// <summary>Enumerate every file, then scan each (the conventional crash-resilient composition).</summary>
    public IEnumerable<PluginDescriptor> Scan(IEnumerable<string> searchPaths) => EnumerateFiles(searchPaths).SelectMany(ScanFile);

    public IEnumerable<PluginDescriptor> ScanFile(string file)
    {
        if (!NativeLibrary.TryLoad(file, out var lib)) yield break;
        try
        {
            foreach (var d in EnumerateDescriptors(lib, file))
                yield return d;
        }
        finally { NativeLibrary.Free(lib); }
    }

    public IHostedPlugin Load(PluginDescriptor descriptor, AudioConfig config)
    {
        if (descriptor.Format != Name)
            throw new ArgumentException($"Not a LADSPA descriptor: {descriptor.Format}", nameof(descriptor));

        var lib = NativeLibrary.Load(descriptor.Path);
        try
        {
            var descFn = Marshal.GetDelegateForFunctionPointer<LadspaInterop.DescriptorFn>(
                NativeLibrary.GetExport(lib, LadspaInterop.DescriptorExport));

            for (nuint i = 0; ; i++)
            {
                var ptr = descFn(i);
                if (ptr == IntPtr.Zero) break;
                var raw = Marshal.PtrToStructure<LadspaInterop.LADSPA_Descriptor>(ptr);
                if (raw.UniqueID.ToString() == descriptor.Id)
                    return new LadspaPlugin(lib, ptr, raw, descriptor, config);   // plugin owns lib
            }
            throw new InvalidOperationException($"LADSPA UniqueID {descriptor.Id} not found in {descriptor.Path}.");
        }
        catch
        {
            NativeLibrary.Free(lib);
            throw;
        }
    }

    // Enumerate ladspa_descriptor(0,1,2,…) until null, projecting each to a PluginDescriptor.
    private static IEnumerable<PluginDescriptor> EnumerateDescriptors(IntPtr lib, string file)
    {
        if (!NativeLibrary.TryGetExport(lib, LadspaInterop.DescriptorExport, out var export))
            yield break;

        var descFn = Marshal.GetDelegateForFunctionPointer<LadspaInterop.DescriptorFn>(export);
        for (nuint i = 0; ; i++)
        {
            IntPtr ptr;
            try { ptr = descFn(i); }
            catch { yield break; }
            if (ptr == IntPtr.Zero) yield break;

            var raw = Marshal.PtrToStructure<LadspaInterop.LADSPA_Descriptor>(ptr);
            yield return new PluginDescriptor(
                Format: "LADSPA",
                Id: raw.UniqueID.ToString(),
                Name: LadspaInterop.Str(raw.Name),
                Vendor: LadspaInterop.Str(raw.Maker),
                IsInstrument: false,          // LADSPA is effects-only
                Path: file);
        }
    }
}
