using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static MidiSharp.Hosting.Clap.ClapAbi;

namespace MidiSharp.Hosting.Clap;

/// <summary>
/// The host side of the CLAP contract: an unmanaged <c>clap_host</c> the plugin is given at creation.
/// Minimal by design — <c>get_extension</c> returns null for every host extension (a conforming plugin
/// must tolerate their absence), and the request callbacks are no-ops. Lives for the process; one shared
/// instance is enough for parameter-only effect hosting.
/// </summary>
internal sealed unsafe class ClapHost : IDisposable
{
    private ClapHost_Native* _host;
    private readonly IntPtr _name, _vendor, _url, _version;

    // Mirror of clap_host with our callback fields typed so we can install &Method addresses.
    [StructLayout(LayoutKind.Sequential)]
    private struct ClapHost_Native
    {
        public ClapVersion Version;
        public void* HostData;
        public byte* Name, Vendor, Url, HostVersion;
        public delegate* unmanaged[Cdecl]<ClapHost_Native*, byte*, void*> GetExtension;
        public delegate* unmanaged[Cdecl]<ClapHost_Native*, void> RequestRestart;
        public delegate* unmanaged[Cdecl]<ClapHost_Native*, void> RequestProcess;
        public delegate* unmanaged[Cdecl]<ClapHost_Native*, void> RequestCallback;
    }

    public ClapHost()
    {
        _name = Utf8("MidiSharp");
        _vendor = Utf8("MidiSharp");
        _url = Utf8("https://github.com/lxman/MidiSharp");
        _version = Utf8("0.10.0");

        _host = (ClapHost_Native*)NativeMemory.AllocZeroed((nuint)sizeof(ClapHost_Native));
        _host->Version = ClapVersion.Current;
        _host->HostData = null;
        _host->Name = (byte*)_name;
        _host->Vendor = (byte*)_vendor;
        _host->Url = (byte*)_url;
        _host->HostVersion = (byte*)_version;
        _host->GetExtension = &HostGetExtension;
        _host->RequestRestart = &HostRequestRestart;
        _host->RequestProcess = &HostRequestProcess;
        _host->RequestCallback = &HostRequestCallback;
    }

    /// <summary>The unmanaged <c>clap_host*</c> to hand to <c>create_plugin</c>.</summary>
    public ClapAbi.ClapHost* Pointer => (ClapAbi.ClapHost*)_host;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* HostGetExtension(ClapHost_Native* host, byte* extensionId) => null;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostRequestRestart(ClapHost_Native* host) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostRequestProcess(ClapHost_Native* host) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostRequestCallback(ClapHost_Native* host) { }

    private static IntPtr Utf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var p = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        ((byte*)p)[bytes.Length] = 0;
        return p;
    }

    public void Dispose()
    {
        if (_host != null) { NativeMemory.Free(_host); _host = null; }
        Marshal.FreeHGlobal(_name);
        Marshal.FreeHGlobal(_vendor);
        Marshal.FreeHGlobal(_url);
        Marshal.FreeHGlobal(_version);
    }
}
