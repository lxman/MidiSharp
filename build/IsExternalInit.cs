// Single shared copy of the IsExternalInit shim for the whole repo. C# 'init' accessors and records
// need System.Runtime.CompilerServices.IsExternalInit, which the netstandard2.1 BCL lacks. Rather than
// a per-assembly copy, Directory.Build.props compiles THIS file into every netstandard2.1 project, so
// each assembly still gets its own internal definition (no cross-assembly visibility issues) from one
// source of truth. net10.0 projects (tests, hosts) already have the type in their BCL and skip this.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
