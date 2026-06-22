using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MidiSharp.Loader;

/// <summary>
/// A backing store that can warm a byte range ahead of use. Implemented by memory-mapped sources
/// so a sample about to play can be paged in asynchronously before the audio thread reads it.
/// </summary>
internal interface IPrefetchable
{
    /// <summary>Hint the OS to read the given region into the page cache (non-blocking).</summary>
    void Prefetch(ReadOnlyMemory<byte> region);
}

/// <summary>
/// Exposes a whole file, memory-mapped read-only, as a <see cref="ReadOnlyMemory{T}"/> of bytes.
/// Sample data parsed from this view lives in the OS file cache rather than on the GC heap — a
/// large SoundFont no longer commits its sample pool to managed memory, the working set tracks the
/// pages actually touched, and the GC never scans it.
/// </summary>
/// <remarks>
/// <para>
/// LIFETIME: the exposed memory is valid only until <see cref="Dispose"/>. Whatever holds a
/// <see cref="ReadOnlyMemory{T}"/> slice into it (the SF2 sample source) owns this manager and must
/// dispose it ONLY AFTER the audio output is stopped — a live audio-thread read after the view is
/// unmapped is a native use-after-free, not a benign managed dangling reference. The finalizer and
/// the SafeHandle-based members self-release, so a forgotten Dispose leaks nothing permanently.
/// </para>
/// <para>
/// 32-bit length: a <see cref="Span{T}"/> is int-indexed, so files larger than 2 GB cannot be
/// mapped as a single span; the loader falls back to a managed read for those (the same 2 GB
/// ceiling a managed <c>byte[]</c> already has).
/// </para>
/// </remarks>
internal sealed unsafe class MemoryMappedFileManager : MemoryManager<byte>, IPrefetchable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly SafeMemoryMappedViewHandle _handle;
    private readonly byte* _pointer;
    private readonly int _length;
    private bool _disposed;

    public MemoryMappedFileManager(string path)
    {
        long length = new FileInfo(path).Length;
        if (length > int.MaxValue)
            throw new NotSupportedException($"File too large to memory-map as a single span: {length} bytes");
        _length = (int)length;

        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0,
            MemoryMappedFileAccess.Read);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _handle = _view.SafeMemoryMappedViewHandle;

        byte* p = null;
        _handle.AcquirePointer(ref p);
        _pointer = p + _view.PointerOffset;   // PointerOffset is 0 for an offset-0 view
    }

    ~MemoryMappedFileManager() => Dispose(false);

    public override Span<byte> GetSpan() => new(_pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        return new MemoryHandle(_pointer + elementIndex);   // mmap memory is fixed; no GC pin needed
    }

    public override void Unpin() { }

    /// <summary>
    /// Asynchronously page the region into the OS cache (advisory; demand paging still works if it
    /// fails or the platform is unsupported). Pinning the slice resolves its absolute address in the
    /// mapping; the syscall returns immediately without blocking on I/O.
    /// </summary>
    public void Prefetch(ReadOnlyMemory<byte> region)
    {
        if (_disposed || region.IsEmpty) return;
        using MemoryHandle h = region.Pin();
        OsPrefetch((IntPtr)h.Pointer, region.Length);
    }

    private const int MADV_WILLNEED = 3;   // same value on Linux and macOS

    private static void OsPrefetch(IntPtr addr, long length)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // madvise requires a page-aligned address; round down and extend the length.
                long page = Environment.SystemPageSize;
                var a = addr.ToInt64();
                long aligned = a & ~(page - 1);
                madvise((IntPtr)aligned, (UIntPtr)(length + (a - aligned)), MADV_WILLNEED);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var entry = new WIN32_MEMORY_RANGE_ENTRY { VirtualAddress = addr, NumberOfBytes = (UIntPtr)length };
                PrefetchVirtualMemory(GetCurrentProcess(), (UIntPtr)1, [entry], 0);
            }
        }
        catch { /* advisory only */ }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int madvise(IntPtr addr, UIntPtr length, int advice);

    [StructLayout(LayoutKind.Sequential)]
    private struct WIN32_MEMORY_RANGE_ENTRY
    {
        public IntPtr VirtualAddress;
        public UIntPtr NumberOfBytes;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool PrefetchVirtualMemory(IntPtr hProcess, UIntPtr numberOfEntries,
        WIN32_MEMORY_RANGE_ENTRY[] entries, uint flags);

    [DllImport("kernel32")]
    private static extern IntPtr GetCurrentProcess();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        try { _handle.ReleasePointer(); } catch { /* handle may already be finalized */ }
        _view.Dispose();
        _file.Dispose();
    }
}
