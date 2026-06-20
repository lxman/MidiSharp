using System;
using System.Runtime.InteropServices;

namespace MidiSharp.Hosting;

/// <summary>
/// A fixed-length block of unmanaged float memory, allocated once (off the audio thread) and reused
/// every block. Hosted plugins keep their planar channel scratch and parameter cells here so the
/// realtime path never touches the managed heap (host invariant: zero managed allocation per block).
/// </summary>
public sealed unsafe class UnmanagedFloatBuffer : IDisposable
{
    private float* _ptr;

    public UnmanagedFloatBuffer(int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
        // Zeroed so an un-written channel reads as silence rather than garbage.
        _ptr = length == 0 ? null : (float*)NativeMemory.AllocZeroed((nuint)length, (nuint)sizeof(float));
    }

    public int Length { get; }

    /// <summary>Raw pointer to the block (for native calls / <see cref="PlanarBuffers"/>).</summary>
    public float* Pointer => _ptr;

    /// <summary>The block as a managed span. Length = <see cref="Length"/>.</summary>
    public Span<float> Span => new(_ptr, Length);

    /// <summary>Zero the block (e.g. before an accumulating pass).</summary>
    public void Clear() => Span.Clear();

    public void Dispose()
    {
        if (_ptr != null)
        {
            NativeMemory.Free(_ptr);
            _ptr = null;
        }
    }
}
