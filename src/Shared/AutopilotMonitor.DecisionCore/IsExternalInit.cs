// netstandard2.0 polyfill: the C# 9 compiler requires this marker type to emit
// init-only property setters (records / `with`-expressions). It ships in the BCL
// from .NET 5 onward but not in netstandard2.0. Internal on purpose — consumers
// (Agent net48, Backend net8.0) never need it: init/`with` usage stays inside
// this assembly, and net8.0 consumers resolve the BCL type anyway.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
