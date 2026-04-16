// Polyfill for C# 9 init-only properties on runtimes that lack IsExternalInit
// (Unity's .NET Standard 2.1 / Mono).
// ReSharper disable once CheckNamespace

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
