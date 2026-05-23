// Polyfill for C# 9 init-only properties on runtimes that lack IsExternalInit
// (Unity's .NET Standard 2.1 / Mono). Public so any assembly referencing
// com.trecs.core can use init-only setters — including the sample assemblies,
// which need it for the [Immutable]-friendly readonly-struct + init-property
// pattern in e.g. HeightmapDescriptor.
// ReSharper disable once CheckNamespace

namespace System.Runtime.CompilerServices
{
    public static class IsExternalInit { }
}
