using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Trecs.SourceGen.Performance
{
    /// <summary>
    /// High-performance caching system for expensive symbol operations.
    /// Reduces repeated symbol analysis and string generation during source generation.
    /// </summary>
    internal static class PerformanceCache
    {
        private static readonly ConcurrentDictionary<ISymbol, string> _displayStringCache = new(
            SymbolEqualityComparer.Default
        );

        private static readonly ConcurrentDictionary<ISymbol, AttributeData[]> _attributeCache =
            new(SymbolEqualityComparer.Default);

        // Note: We cannot cache distinct types because order preservation is required
        // and the order may vary for the same set of types depending on context

        /// <summary>
        /// Gets cached display string for a symbol, computing it only once.
        /// </summary>
        public static string GetDisplayString(ISymbol symbol)
        {
            if (symbol == null)
                return string.Empty;

            return _displayStringCache.GetOrAdd(symbol, s => s.ToDisplayString());
        }

        /// <summary>
        /// Gets cached attributes for a symbol, computing them only once.
        /// </summary>
        public static AttributeData[] GetAttributes(ISymbol symbol)
        {
            if (symbol == null)
                return Array.Empty<AttributeData>();

            return _attributeCache.GetOrAdd(symbol, s => s.GetAttributes().ToArray());
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
        /// Finds attribute by name with caching for better performance.
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
        /// Checks if symbol has attribute by name with caching.
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

        /// <summary>
        /// Clears all caches. Should be called when compilation context changes.
        /// </summary>
        public static void ClearCaches()
        {
            _displayStringCache.Clear();
            _attributeCache.Clear();
        }

        /// <summary>
        /// Gets cache statistics for debugging and performance monitoring.
        /// </summary>
        public static CacheStatistics GetStatistics()
        {
            return new CacheStatistics
            {
                DisplayStringCacheSize = _displayStringCache.Count,
                AttributeCacheSize = _attributeCache.Count,
            };
        }
    }

    /// <summary>
    /// Statistics about cache usage for performance monitoring.
    /// </summary>
    internal class CacheStatistics
    {
        public int DisplayStringCacheSize { get; set; }
        public int AttributeCacheSize { get; set; }

        public override string ToString()
        {
            return $"SymbolCache Stats - DisplayStrings: {DisplayStringCacheSize}, "
                + $"Attributes: {AttributeCacheSize}";
        }
    }
}
