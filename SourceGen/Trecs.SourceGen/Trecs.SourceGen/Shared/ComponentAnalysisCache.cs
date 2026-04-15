using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Thread-safe cache for expensive component type analysis operations
    /// (unwrap detection, entity component checks, property name generation).
    /// Distinct from <see cref="Performance.PerformanceCache"/> which caches
    /// general symbol display strings and attributes.
    /// </summary>
    internal static class ComponentAnalysisCache
    {
        private static readonly ConcurrentDictionary<ITypeSymbol, bool> _unwrapComponentCache = new(
            SymbolEqualityComparer.Default
        );

        private static readonly ConcurrentDictionary<ITypeSymbol, bool> _entityComponentCache = new(
            SymbolEqualityComparer.Default
        );

        private static readonly ConcurrentDictionary<ITypeSymbol, IFieldSymbol?> _unwrapFieldCache =
            new(SymbolEqualityComparer.Default);

        private static readonly ConcurrentDictionary<ITypeSymbol, string> _propertyNameCache = new(
            SymbolEqualityComparer.Default
        );

        private static readonly ConcurrentDictionary<
            ITypeSymbol,
            (ITypeSymbol FinalType, bool WasUnwrapped)
        > _unwrapCache = new(SymbolEqualityComparer.Default);

        /// <summary>
        /// Cached check if a type is marked with Unwrap attribute
        /// </summary>
        public static bool IsUnwrapComponent(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol namedType)
                return false;

            return _unwrapComponentCache.GetOrAdd(
                namedType,
                t =>
                    t.GetAttributes()
                        .Any(attr =>
                            attr.AttributeClass?.Name == TrecsAttributeNames.Unwrap
                            && PerformanceCache.GetDisplayString(
                                attr.AttributeClass?.ContainingNamespace
                            ) == TrecsNamespaces.Trecs
                        )
            );
        }

        /// <summary>
        /// Cached check if a type implements IEntityComponent
        /// </summary>
        public static bool IsEntityComponent(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol namedType)
                return false;

            return _entityComponentCache.GetOrAdd(
                namedType,
                t => SymbolAnalyzer.ImplementsInterface(namedType, "IEntityComponent")
            );
        }

        /// <summary>
        /// Cached lookup of the single field from a UnwrapComponent
        /// </summary>
        public static IFieldSymbol? GetUnwrapComponentField(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol namedType || !IsUnwrapComponent(namedType))
                return null;

            return _unwrapFieldCache.GetOrAdd(
                namedType,
                t =>
                {
                    var fields = t.GetMembers()
                        .OfType<IFieldSymbol>()
                        .Where(f => !f.IsStatic && !f.IsConst)
                        .ToList();

                    return fields.Count == 1 ? fields[0] : null;
                }
            );
        }

        /// <summary>
        /// Cached property name generation for component types
        /// </summary>
        public static string GetPropertyName(ITypeSymbol componentType)
        {
            return _propertyNameCache.GetOrAdd(componentType, GeneratePropertyName);
        }

        /// <summary>
        /// Cached unwrapping of Unwrap component types
        /// </summary>
        public static (ITypeSymbol FinalType, bool WasUnwrapped) UnwrapComponent(
            ITypeSymbol componentType
        )
        {
            return _unwrapCache.GetOrAdd(
                componentType,
                t =>
                    UnwrapComponentInternal(
                        t,
                        new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)
                    )
            );
        }

        /// <summary>
        /// Clear all caches (useful for testing or memory management)
        /// </summary>
        public static void ClearCaches()
        {
            _unwrapComponentCache.Clear();
            _entityComponentCache.Clear();
            _unwrapFieldCache.Clear();
            _propertyNameCache.Clear();
            _unwrapCache.Clear();
        }

        private static string GeneratePropertyName(ITypeSymbol componentType)
        {
            // For generic types, include type arguments to avoid conflicts
            var typeName = componentType.Name;

            if (componentType is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                // For generic types like Interpolated<CFppCameraDesiredOffset>, build "InterpolatedFppCameraDesiredOffset"
                // Then process each generic argument
                var genericArgs = namedType
                    .TypeArguments.Select(arg => GetPropertyNameForNestedType(arg))
                    .ToArray();
                typeName = typeName + string.Join("", genericArgs);
            }
            else
            {
                // Handle nested types (e.g., SalamanderView.CState -> SalamanderViewState)
                typeName = GetPropertyNameForNestedType(componentType);
            }

            return typeName;
        }

        private static string GetPropertyNameForNestedType(ITypeSymbol type)
        {
            var parts = new List<string>();

            // Walk up the containment chain
            var current = type;
            while (current != null)
            {
                var name = current.Name;

                parts.Insert(0, name); // Insert at beginning to maintain order

                // Move to containing type
                current = current.ContainingType;
            }

            return string.Join("", parts);
        }

        private static (ITypeSymbol FinalType, bool WasUnwrapped) UnwrapComponentInternal(
            ITypeSymbol componentType,
            HashSet<ITypeSymbol> visitedTypes
        )
        {
            // Prevent infinite recursion
            if (!visitedTypes.Add(componentType))
            {
                return (componentType, false);
            }

            if (componentType is not INamedTypeSymbol namedType || !IsUnwrapComponent(namedType))
            {
                return (componentType, false);
            }

            var field = GetUnwrapComponentField(namedType);
            if (field == null)
            {
                return (componentType, false);
            }

            var (finalType, wasUnwrapped) = UnwrapComponentInternal(field.Type, visitedTypes);
            return (finalType, true);
        }
    }
}
