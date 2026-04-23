using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Extracts component types from IRead/IWrite interfaces and aspect-interface base
    /// interfaces (interfaces that themselves extend Trecs.IAspect) on a given type symbol.
    /// </summary>
    internal static class InterfaceComponentExtractor
    {
        /// <summary>
        /// Inspects the direct interfaces of a type symbol and extracts:
        /// - IRead type arguments → readTypes
        /// - IWrite type arguments → writeTypes
        /// - Interfaces that extend Trecs.IAspect → aspectInterfaceTypes
        /// </summary>
        public static void ExtractComponentsFromInterfaces(
            ITypeSymbol symbol,
            List<ITypeSymbol> readTypes,
            List<ITypeSymbol> writeTypes,
            List<ITypeSymbol> aspectInterfaceTypes
        )
        {
            if (symbol is not INamedTypeSymbol namedSymbol)
                return;

            foreach (var iface in namedSymbol.Interfaces)
            {
                var originalDef = iface.OriginalDefinition;
                var ifaceName = originalDef.Name;
                var containingNs = PerformanceCache.GetDisplayString(
                    originalDef.ContainingNamespace
                );

                if (containingNs == "Trecs" && ifaceName == "IRead")
                {
                    foreach (var typeArg in iface.TypeArguments)
                        readTypes.Add(typeArg);
                }
                else if (containingNs == "Trecs" && ifaceName == "IWrite")
                {
                    foreach (var typeArg in iface.TypeArguments)
                        writeTypes.Add(typeArg);
                }
                else if (containingNs == "Trecs" && ifaceName == "IAspect")
                {
                    // Marker interface — skip (no cascade of components from IAspect itself)
                }
                else if (SymbolAnalyzer.IsAspectInterface(iface))
                {
                    aspectInterfaceTypes.Add(iface);
                }
            }
        }
    }
}
