// Polyfill for init-only properties on netstandard2.1.
#if NETSTANDARD2_0 || NETSTANDARD2_1
namespace System.Runtime.CompilerServices
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
#endif
