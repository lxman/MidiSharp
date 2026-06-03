// Enables C# 'init' accessors and records when targeting netstandard2.1, whose BCL lacks
// System.Runtime.CompilerServices.IsExternalInit. Internal so it never clashes with the
// equivalent shim in other assemblies (e.g. MidiSharp.Core).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
