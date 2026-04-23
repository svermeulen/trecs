using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Aspect
{
    /// <summary>
    /// Utility class for parsing aspect interfaces (interfaces that extend Trecs.IAspect)
    /// and cascading their IRead/IWrite declarations. Used by AspectAttributeParser to
    /// merge components from an aspect struct's direct aspect-interface bases, and by the
    /// incremental generator to parse an aspect-interface symbol directly for codegen.
    /// </summary>
    internal static class AspectInterfaceParser
    {
        /// <summary>
        /// Walks a set of aspect-interface roots and accumulates the cascaded IRead/IWrite
        /// component types into the provided output lists. The <c>visitedInterfaces</c> set
        /// collapses diamond inheritance (same aspect interface reached via two paths contributes
        /// its components only once) and also bounds recursion in the unlikely event that Roslyn
        /// surfaces a cyclic semantic graph — C# itself rejects aspect-interface cycles
        /// (CS0529), so this is defensive only.
        /// </summary>
        public static void ExtractInterfaceComponents(
            IEnumerable<ITypeSymbol> interfaceTypes,
            List<ITypeSymbol> readTypes,
            List<ITypeSymbol> writeTypes
        )
        {
            var visitedInterfaces = new HashSet<string>();

            foreach (var interfaceType in interfaceTypes)
            {
                ExtractInterfaceComponentsRecursive(
                    interfaceType,
                    readTypes,
                    writeTypes,
                    visitedInterfaces
                );
            }
        }

        private static void ExtractInterfaceComponentsRecursive(
            ITypeSymbol interfaceType,
            List<ITypeSymbol> readTypes,
            List<ITypeSymbol> writeTypes,
            HashSet<string> visitedInterfaces
        )
        {
            var interfaceName = PerformanceCache.GetDisplayString(interfaceType);

            if (!visitedInterfaces.Add(interfaceName))
                return;

            var interfaceData = ParseAspectInterface(interfaceType);
            if (interfaceData == null)
                return;

            readTypes.AddRange(interfaceData.ReadTypes);
            writeTypes.AddRange(interfaceData.WriteTypes);

            foreach (var nestedInterface in interfaceData.InterfaceTypes)
            {
                ExtractInterfaceComponentsRecursive(
                    nestedInterface,
                    readTypes,
                    writeTypes,
                    visitedInterfaces
                );
            }
        }

        /// <summary>
        /// Parses aspect-interface data from a type symbol. Returns null if the symbol is not
        /// an aspect interface (i.e. an interface extending Trecs.IAspect).
        /// Extracts component types from IRead/IWrite interfaces and nested aspect interfaces
        /// from C# base interfaces.
        /// </summary>
        /// <param name="interfaceSymbol">The interface type symbol to parse</param>
        /// <returns>Parsed data, or null if the symbol is not an aspect interface</returns>
        public static AspectInterfaceData? ParseAspectInterface(ITypeSymbol interfaceSymbol)
        {
            if (!SymbolAnalyzer.IsAspectInterface(interfaceSymbol))
                return null;

            var readTypes = new List<ITypeSymbol>();
            var writeTypes = new List<ITypeSymbol>();
            var interfaceTypes = new List<ITypeSymbol>();

            // Extract Read/Write types from IRead<>/IWrite<> interfaces, and nested aspect interfaces
            InterfaceComponentExtractor.ExtractComponentsFromInterfaces(
                interfaceSymbol,
                readTypes,
                writeTypes,
                interfaceTypes
            );

            return new AspectInterfaceData(readTypes, writeTypes, interfaceTypes);
        }
    }
}
