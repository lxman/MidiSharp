using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MidiSharp.Hosting;
using static MidiSharp.Hosting.Clap.ClapAbi;

namespace MidiSharp.Hosting.Clap;

/// <summary>
/// The host side of the CLAP contract: an unmanaged <c>clap_host</c> the plugin is given at creation. It
/// answers <c>get_extension</c> for the editor run-loop extensions (<c>clap.timer-support</c> and
/// <c>clap.posix-fd-support</c>) when an editor is open, forwarding the plugin's timer/fd registrations to
/// the editor thread's <see cref="IEditorRunLoop"/>; every other host extension is absent (a conforming
/// plugin tolerates that). One host per plugin; a GCHandle stashed in <c>host_data</c> lets the static
/// callbacks recover this instance.
/// </summary>
internal sealed unsafe class ClapHost : IDisposable
{
    private ClapHost_Native* _host;
    private readonly IntPtr _name, _vendor, _url, _version;
    private GCHandle _self;

    // Editor context, set while an editor is open (on the UI thread): the live plugin + its timer/fd
    // extensions + the run loop to forward registrations to.
    private IEditorRunLoop? _loop;
    private ClapAbi.ClapPlugin* _plugin;
    private ClapPluginTimerSupport* _pluginTimer;
    private ClapPluginPosixFdSupport* _pluginFd;
    private uint _nextTimerId;

    // The worker runs process and gui on one thread, so we distinguish by CONTEXT: is_audio_thread is true
    // only while inside a Process() call; everything else (gui, params, state) reads as the main thread.
    private volatile bool _inProcess;
    public void SetInProcess(bool v) => _inProcess = v;

    private static readonly IntPtr TimerSupport = BuildTimerSupport();
    private static readonly IntPtr FdSupport = BuildFdSupport();
    private static readonly IntPtr ThreadCheck = BuildThreadCheck();
    private static readonly IntPtr GuiSupport = BuildGuiSupport();

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

        _self = GCHandle.Alloc(this);
        _host = (ClapHost_Native*)NativeMemory.AllocZeroed((nuint)sizeof(ClapHost_Native));
        _host->Version = ClapVersion.Current;
        _host->HostData = (void*)GCHandle.ToIntPtr(_self);
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

    /// <summary>Bind the open editor's run loop and resolve the plugin's timer/fd extensions so the host can
    /// service its registrations. Called on the UI thread before the editor is created.</summary>
    public void SetEditorContext(ClapAbi.ClapPlugin* plugin, IEditorRunLoop loop)
    {
        _plugin = plugin;
        _loop = loop;
        _pluginTimer = (ClapPluginTimerSupport*)plugin->GetExtension(plugin, FixedExt(ExtTimerSupport));
        _pluginFd = (ClapPluginPosixFdSupport*)plugin->GetExtension(plugin, FixedExt(ExtPosixFdSupport));
    }

    public void ClearEditorContext() { _loop = null; _pluginTimer = null; _pluginFd = null; }

    private static ClapHost Self(ClapHost_Native* host) => (ClapHost)GCHandle.FromIntPtr((IntPtr)host->HostData).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* HostGetExtension(ClapHost_Native* host, byte* extensionId)
    {
        var id = Marshal.PtrToStringUTF8((IntPtr)extensionId);
        if (id == ExtTimerSupport) return (void*)TimerSupport;
        if (id == ExtPosixFdSupport) return (void*)FdSupport;
        if (id == ExtThreadCheck) return (void*)ThreadCheck;
        if (id == ExtGui) return (void*)GuiSupport;
        return null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostRequestRestart(ClapHost_Native* host) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostRequestProcess(ClapHost_Native* host) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostRequestCallback(ClapHost_Native* host) { }

    // ── clap.timer-support ──
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte HostRegisterTimer(ClapAbi.ClapHost* host, uint periodMs, uint* timerId)
    {
        var self = Self((ClapHost_Native*)host);
        if (self._loop == null || self._pluginTimer == null) return 0;
        var id = self._nextTimerId++;
        var plugin = self._plugin;
        var ext = self._pluginTimer;
        self._loop.RegisterTimer(periodMs, id, () => ext->OnTimer(plugin, id));
        if (timerId != null) *timerId = id;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte HostUnregisterTimer(ClapAbi.ClapHost* host, uint timerId)
    {
        Self((ClapHost_Native*)host)._loop?.UnregisterTimer(timerId);
        return 1;
    }

    // ── clap.posix-fd-support ──
    private static byte RegisterFdImpl(ClapAbi.ClapHost* host, int fd, uint flags)
    {
        var self = Self((ClapHost_Native*)host);
        if (self._loop == null || self._pluginFd == null) return 0;
        var plugin = self._plugin;
        var ext = self._pluginFd;
        self._loop.RegisterFd(fd, () => ext->OnFd(plugin, fd, flags));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte HostRegisterFd(ClapAbi.ClapHost* host, int fd, uint flags) => RegisterFdImpl(host, fd, flags);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte HostModifyFd(ClapAbi.ClapHost* host, int fd, uint flags) => RegisterFdImpl(host, fd, flags);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte HostUnregisterFd(ClapAbi.ClapHost* host, int fd)
    {
        Self((ClapHost_Native*)host)._loop?.UnregisterFd(fd);
        return 1;
    }

    // ── clap.thread-check ──
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte HostIsMainThread(ClapAbi.ClapHost* host) => (byte)(Self((ClapHost_Native*)host)._inProcess ? 0 : 1);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte HostIsAudioThread(ClapAbi.ClapHost* host) => (byte)(Self((ClapHost_Native*)host)._inProcess ? 1 : 0);

    // ── clap.gui (host side) ──
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostResizeHintsChanged(ClapAbi.ClapHost* host) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte HostRequestResize(ClapAbi.ClapHost* host, uint width, uint height) => 1;   // accept; our window follows

    private static IntPtr BuildThreadCheck()
    {
        var p = (ClapHostThreadCheck*)NativeMemory.Alloc((nuint)sizeof(ClapHostThreadCheck));
        p->IsMainThread = &HostIsMainThread;
        p->IsAudioThread = &HostIsAudioThread;
        return (IntPtr)p;
    }

    private static IntPtr BuildGuiSupport()
    {
        var p = (ClapHostGui*)NativeMemory.Alloc((nuint)sizeof(ClapHostGui));
        p->ResizeHintsChanged = &HostResizeHintsChanged;
        p->RequestResize = &HostRequestResize;
        return (IntPtr)p;
    }

    private static IntPtr BuildTimerSupport()
    {
        var p = (ClapHostTimerSupport*)NativeMemory.Alloc((nuint)sizeof(ClapHostTimerSupport));
        p->RegisterTimer = &HostRegisterTimer;
        p->UnregisterTimer = &HostUnregisterTimer;
        return (IntPtr)p;
    }

    private static IntPtr BuildFdSupport()
    {
        var p = (ClapHostPosixFdSupport*)NativeMemory.Alloc((nuint)sizeof(ClapHostPosixFdSupport));
        p->RegisterFd = &HostRegisterFd;
        p->ModifyFd = &HostModifyFd;
        p->UnregisterFd = &HostUnregisterFd;
        return (IntPtr)p;
    }

    // Interned, NUL-terminated extension-id strings for querying the plugin's extensions.
    private static readonly System.Collections.Generic.Dictionary<string, IntPtr> Ext = new();
    private static byte* FixedExt(string s)
    {
        lock (Ext)
        {
            if (!Ext.TryGetValue(s, out var p))
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                p = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                ((byte*)p)[bytes.Length] = 0;
                Ext[s] = p;
            }
            return (byte*)p;
        }
    }

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
        if (_self.IsAllocated) _self.Free();
        Marshal.FreeHGlobal(_name);
        Marshal.FreeHGlobal(_vendor);
        Marshal.FreeHGlobal(_url);
        Marshal.FreeHGlobal(_version);
    }
}
