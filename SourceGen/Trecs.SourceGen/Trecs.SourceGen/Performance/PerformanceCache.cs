using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Trecs.SourceGen.Performance
{
    /// <summary>
    /// Helpers for common symbol operations (display strings, attribute lookup, type
    /// deduplication) used throughout the generators.
    ///
    /// Historically this type held static <c>ConcurrentDictionary&lt;ISymbol, …&gt;</c>
    /// caches. Those were removed: keying a process-wide static on <see cref="ISymbol"/>
    /// roots every <c>Compilation</c> the symbol came from for the lifetime of the host
    /// process, so a long-running IDE session would leak unbounded memory. The underlying
    /// operations (<c>ToDisplayString()</c>, <c>GetAttributes()</c>) are cheap enough that
    /// recomputing them is preferable to that leak — and Roslyn already caches symbols and
    /// attribute data per-Compilation internally. The class name is retained to avoid churn
    /// across its many call sites.
    /// </summary>
    internal static class PerformanceCache
    {
        // Note: We cannot cache distinct types because order preservation is required
        // and the order may vary for the same set of types depending on context

        /// <summary>
        /// Gets the display string for a symbol.
        /// </summary>
        public static string GetDisplayString(ISymbol symbol)
        {
            if (symbol == null)
                return string.Empty;

            return symbol.ToDisplayString();
        }

        /// <summary>
        /// Gets the attributes applied to a symbol.
        /// </summary>
        public static AttributeData[] GetAttributes(ISymbol symbol)
        {
            if (symbol == null)
                return Array.Empty<AttributeData>();

            return symbol.GetAttributes().ToArray();
        }

        /// <summary>
        /// Efficiently removes duplicate types using cached sets and optimized comparison.
        /// IMPORTANT: Preserves the original order of types.
        /// </summary>
        public static ImmutableArray<ITypeSymbol> GetDistinctTypes(IEnumerable<ITypeSymbol> types)
        {
            if (types == null)
                return ImmutableArray<ITypeSymbol>.Empty;

            // Fast path for small collections - convert to array for consistent enumeration
            var typesArray = types as ITypeSymbol[] ?? types.ToArray();
            if (typesArray.Length <= 1)
                return typesArray.ToImmutableArray();

            // Use a HashSet to track seen items and a List to preserve order
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            var result = new List<ITypeSymbol>(typesArray.Length);

            foreach (var type in typesArray)
            {
                if (seen.Add(type))
                {
                    result.Add(type);
                }
            }

            return result.ToImmutableArray();
        }

        /// <summary>
        /// Efficiently merges multiple type collections with deduplication.
        /// </summary>
        public static ImmutableArray<ITypeSymbol> MergeDistinctTypes(
            params IEnumerable<ITypeSymbol>[] typeLists
        )
        {
            if (typeLists == null || typeLists.Length == 0)
                return ImmutableArray<ITypeSymbol>.Empty;

            if (typeLists.Length == 1)
                return GetDistinctTypes(typeLists[0]);

            var allTypes = new List<ITypeSymbol>();
            foreach (var typeList in typeLists)
            {
                if (typeList != null)
                {
                    allTypes.AddRange(typeList);
                }
            }

            return GetDistinctTypes(allTypes);
        }

        /// <summary>
        /// Finds an attribute on a symbol by name.
        /// When namespaceName is provided, also verifies the attribute's containing namespace.
        /// </summary>
        public static AttributeData? FindAttributeByName(
            ISymbol symbol,
            string attributeName,
            string? namespaceName = null
        )
        {
            if (symbol == null || string.IsNullOrEmpty(attributeName))
                return null;

            var attributes = GetAttributes(symbol);
            return Array.Find(
                attributes,
                attr =>
                    attr.AttributeClass?.Name == attributeName
                    && (
                        namespaceName == null
                        || PerformanceCache.GetDisplayString(
                            attr.AttributeClass?.ContainingNamespace
                        ) == namespaceName
                    )
            );
        }

        /// <summary>
        /// Checks if a symbol has an attribute by name.
        /// When namespaceName is provided, also verifies the attribute's containing namespace.
        /// </summary>
        public static bool HasAttributeByName(
            ISymbol symbol,
            string attributeName,
            string? namespaceName = null
        )
        {
            return FindAttributeByName(symbol, attributeName, namespaceName) != null;
        }
    }
}
