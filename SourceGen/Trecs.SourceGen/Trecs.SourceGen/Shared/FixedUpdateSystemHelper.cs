#nullable enable

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Trecs.SourceGen.Shared
{
    internal static class FixedUpdateSystemHelper
    {
        const string TrecsNamespace = "Trecs";
        const string ISystemName = "ISystem";
        const string ExecuteInAttributeName = "ExecuteInAttribute";
        const int FixedPhaseValue = 1; // SystemPhase.Fixed

        public static bool IsFixedUpdateSystem(
            INamedTypeSymbol type,
            ConcurrentDictionary<INamedTypeSymbol, bool> cache
        )
        {
            if (cache.TryGetValue(type, out var cached))
                return cached;

            var result = ComputeIsFixedUpdateSystem(type);
            cache[type] = result;
            return result;
        }

        public static INamedTypeSymbol? GetContainingNamedType(ISymbol? symbol)
        {
            while (symbol != null)
            {
                if (symbol is INamedTypeSymbol named)
                    return named;
                symbol = symbol.ContainingSymbol;
            }
            return null;
        }

        static bool ComputeIsFixedUpdateSystem(INamedTypeSymbol type)
        {
            if (!ImplementsISystem(type))
                return false;

            foreach (var attr in type.GetAttributes())
            {
                var ac = attr.AttributeClass;
                if (ac == null)
                    continue;
                if (ac.Name != ExecuteInAttributeName)
                    continue;
                if (ac.ContainingNamespace?.ToDisplayString() != TrecsNamespace)
                    continue;

                if (attr.ConstructorArguments.Length > 0)
                {
                    var phaseArg = attr.ConstructorArguments[0];
                    if (phaseArg.Value is int phaseValue && phaseValue != FixedPhaseValue)
                        return false;
                }

                return true;
            }

            // No [ExecuteIn] attribute — default is Fixed
            return true;
        }

        static bool ImplementsISystem(INamedTypeSymbol type)
        {
            foreach (var iface in type.AllInterfaces)
            {
                if (
                    iface.Name == ISystemName
                    && iface.ContainingNamespace?.ToDisplayString() == TrecsNamespace
                )
                {
                    return true;
                }
            }
            return false;
        }
    }
}
