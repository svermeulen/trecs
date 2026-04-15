#nullable enable

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Reads assembly-level [TrecsSourceGenSettings] configuration.
    /// Caches results per assembly for performance.
    /// </summary>
    internal static class SourceGenSettingsProvider
    {
        // Sentinel value to distinguish "looked up, found null" from "never looked up"
        private const string NoPrefixSentinel = "\0__none__";

        private static readonly ConcurrentDictionary<IAssemblySymbol, string> _prefixCache =
            new(SymbolEqualityComparer.Default);

        /// <summary>
        /// Gets the configured component prefix for the assembly that declares the given type.
        /// Returns null if no prefix is configured.
        /// </summary>
        public static string? GetComponentPrefixForType(ITypeSymbol type)
        {
            var assembly = type.ContainingAssembly;
            if (assembly == null)
                return null;

            var cached = _prefixCache.GetOrAdd(assembly, static asm => LookupPrefix(asm) ?? NoPrefixSentinel);
            return cached == NoPrefixSentinel ? null : cached;
        }

        private static string? LookupPrefix(IAssemblySymbol assembly)
        {
            foreach (var attr in assembly.GetAttributes())
            {
                if (attr.AttributeClass?.Name != TrecsAttributeNames.SourceGenSettings)
                    continue;

                var ns = attr.AttributeClass?.ContainingNamespace;
                if (ns == null || PerformanceCache.GetDisplayString(ns) != TrecsNamespaces.Trecs)
                    continue;

                foreach (var namedArg in attr.NamedArguments)
                {
                    if (namedArg.Key == "ComponentPrefix"
                        && namedArg.Value.Value is string prefix)
                    {
                        return prefix;
                    }
                }

                return null;
            }

            return null;
        }
    }
}
