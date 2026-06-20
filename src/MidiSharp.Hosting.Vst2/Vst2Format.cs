using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.Vst2.Vst2Abi;

namespace MidiSharp.Hosting.Vst2;

/// <summary>
/// The VST2 format adapter: discovers VST2 shared libraries on the VST search path and loads one by its
/// unique id. VST2 exposes no separate metadata API, so a scan instantiates each plugin to read its
/// name/id/flags, then closes it.
/// </summary>
public sealed unsafe class Vst2Format : IPluginFormat
{
    public string Name => "VST2";

    public IEnumerable<string> DefaultSearchPaths
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("VST_PATH");
            if (!string.IsNullOrEmpty(env))
            {
                foreach (var p in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    yield return p;
                yield break;
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".vst");
            yield return "/usr/lib/vst";
            yield return "/usr/lib/lxvst";
            yield return "/usr/local/lib/vst";
            var common = Environment.GetEnvironmentVariable("CommonProgramFiles");
            if (!string.IsNullOrEmpty(common)) yield return Path.Combine(common, "VST2");
        }
    }

    private static string[] Extensions => OperatingSystem.IsWindows() ? ["*.dll"] : ["*.so"];

    public IEnumerable<PluginDescriptor> Scan(IEnumerable<string> searchPaths)
    {
        foreach (var dir in searchPaths)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var ext in Extensions)
                foreach (var file in Directory.EnumerateFiles(dir, ext))
                {
                    var d = ScanFile(file);
                    if (d != null) yield return d;
                }
        }
    }

    private static PluginDescriptor? ScanFile(string file)
    {
        if (!NativeLibrary.TryLoad(file, out var lib)) return null;
        try
        {
            var eff = Instantiate(lib);
            if (eff == null || eff->Magic != EffectMagic) return null;
            try
            {
                return new PluginDescriptor(
                    Format: "VST2",
                    Id: eff->UniqueID.ToString(),
                    Name: EffectName(eff),
                    Vendor: VendorString(eff),
                    IsInstrument: (eff->Flags & FlagsIsSynth) != 0,
                    Path: file);
            }
            finally { eff->Dispatcher(eff, EffClose, 0, IntPtr.Zero, null, 0); }
        }
        finally { NativeLibrary.Free(lib); }
    }

    public IHostedPlugin Load(PluginDescriptor descriptor, AudioConfig config)
    {
        if (descriptor.Format != Name)
            throw new ArgumentException($"Not a VST2 descriptor: {descriptor.Format}", nameof(descriptor));

        Vst2Host.SampleRate = config.SampleRate;
        Vst2Host.BlockSize = config.MaxBlockFrames;

        var lib = NativeLibrary.Load(descriptor.Path);
        try
        {
            var eff = Instantiate(lib);
            if (eff == null || eff->Magic != EffectMagic)
                throw new InvalidOperationException($"'{descriptor.Path}' is not a valid VST2 plugin.");
            if (eff->UniqueID.ToString() != descriptor.Id)
            {
                eff->Dispatcher(eff, EffClose, 0, IntPtr.Zero, null, 0);
                throw new InvalidOperationException($"VST2 uniqueID {descriptor.Id} not found in {descriptor.Path}.");
            }
            return new Vst2Plugin(lib, eff, descriptor, config);   // wrapper owns lib + closes the effect
        }
        catch
        {
            NativeLibrary.Free(lib);
            throw;
        }
    }

    // Call VSTPluginMain/main with the host callback and open the effect.
    private static AEffect* Instantiate(IntPtr lib)
    {
        IntPtr export = IntPtr.Zero;
        foreach (var name in EntryExports)
            if (NativeLibrary.TryGetExport(lib, name, out export)) break;
        if (export == IntPtr.Zero) return null;

        var main = (delegate* unmanaged[Cdecl]<IntPtr, AEffect*>)export;
        var eff = main(Vst2Host.Callback);
        if (eff == null || eff->Magic != EffectMagic) return eff;
        eff->Dispatcher(eff, EffOpen, 0, IntPtr.Zero, null, 0);
        return eff;
    }

    internal static string EffectName(AEffect* eff) => StringOp(eff, EffGetEffectName, 0, 256);
    private static string VendorString(AEffect* eff) => StringOp(eff, EffGetVendorString, 0, 256);

    // Dispatcher string queries write an ANSI string into a caller buffer; read it back.
    internal static string StringOp(AEffect* eff, int opcode, int index, int capacity)
    {
        var buf = (byte*)NativeMemory.AllocZeroed((nuint)capacity);
        try
        {
            eff->Dispatcher(eff, opcode, index, IntPtr.Zero, buf, 0);
            return ReadCString(buf, capacity);
        }
        finally { NativeMemory.Free(buf); }
    }
}
