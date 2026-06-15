using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MidiSharp.SoundBank;

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
internal sealed unsafe class MemoryMappedFileManager : MemoryManager<byte>
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly SafeMemoryMappedViewHandle _handle;
    private readonly byte* _pointer;
    private readonly int _length;
    private bool _disposed;

    public MemoryMappedFileManager(string path)
    {
        var length = new FileInfo(path).Length;
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

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        try { _handle.ReleasePointer(); } catch { /* handle may already be finalized */ }
        _view.Dispose();
        _file.Dispose();
    }
}
