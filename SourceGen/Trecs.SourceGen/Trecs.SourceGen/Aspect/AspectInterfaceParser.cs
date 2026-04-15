using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Aspect
{
    /// <summary>
    /// Utility class for parsing AspectInterface attributes and extracting component types.
    /// This eliminates code duplication between AspectAttributeParser and ForEachAspectGenerator.
    /// </summary>
    internal static class AspectInterfaceParser
    {
        /// <summary>
        /// Extracts component types from AspectInterface attributes for a list of interface types.
        /// Includes circular reference detection to prevent infinite loops.
        /// </summary>
        /// <param name="interfaceTypes">The interface types to parse</param>
        /// <param name="readTypes">Output list to add read component types to</param>
        /// <param name="writeTypes">Output list to add write component types to</param>
        /// <param name="reportDiagnostic">Optional diagnostic reporter for circular references</param>
        /// <param name="location">Optional location for error reporting</param>
        public static void ExtractInterfaceComponents(
            IEnumerable<ITypeSymbol> interfaceTypes,
            List<ITypeSymbol> readTypes,
            List<ITypeSymbol> writeTypes,
            Action<Diagnostic>? reportDiagnostic = null,
            Location? location = null
        )
        {
            var visitedInterfaces = new HashSet<string>();
            var processingStack = new Stack<string>();

            foreach (var interfaceType in interfaceTypes)
            {
                ExtractInterfaceComponentsRecursive(
                    interfaceType,
                    readTypes,
                    writeTypes,
                    visitedInterfaces,
                    processingStack,
                    reportDiagnostic,
                    location
                );
            }
        }

        /// <summary>
        /// Recursive helper method for extracting interface components with circular reference detection.
        /// </summary>
        private static void ExtractInterfaceComponentsRecursive(
            ITypeSymbol interfaceType,
            List<ITypeSymbol> readTypes,
            List<ITypeSymbol> writeTypes,
            HashSet<string> visitedInterfaces,
            Stack<string> processingStack,
            Action<Diagnostic>? reportDiagnostic,
            Location? location
        )
        {
            var interfaceName = PerformanceCache.GetDisplayString(interfaceType);

            // Check for circular reference
            if (processingStack.Contains(interfaceName))
            {
                var cyclePath =
                    string.Join(" -> ", processingStack.Reverse()) + " -> " + interfaceName;

                if (reportDiagnostic != null && location != null)
                {
                    var diagnostic = Diagnostic.Create(
                        DiagnosticDescriptors.CircularAspectInterfaceReference,
                        location,
                        cyclePath
                    );
                    reportDiagnostic(diagnostic);
                }

                SourceGenLogger.Log(
                    $"[AspectInterfaceParser] Circular reference detected: {cyclePath}"
                );
                return;
            }

            // Skip if already fully processed
            if (visitedInterfaces.Contains(interfaceName))
            {
                return;
            }

            // Add to processing stack
            processingStack.Push(interfaceName);

            try
            {
                var interfaceData = ParseAspectInterfaceAttribute(interfaceType);
                if (interfaceData != null)
                {
                    readTypes.AddRange(interfaceData.ReadTypes);
                    writeTypes.AddRange(interfaceData.WriteTypes);

                    // Process nested interfaces recursively
                    if (interfaceData.InterfaceTypes.Length > 0)
                    {
                        foreach (var nestedInterface in interfaceData.InterfaceTypes)
                        {
                            ExtractInterfaceComponentsRecursive(
                                nestedInterface,
                                readTypes,
                                writeTypes,
                                visitedInterfaces,
                                processingStack,
                                reportDiagnostic,
                                location
                            );
                        }
                    }
                }

                // Mark as fully processed
                visitedInterfaces.Add(interfaceName);
            }
            finally
            {
                // Remove from processing stack
                processingStack.Pop();
            }
        }

        /// <summary>
        /// Extracts all component types (read and write) from AspectInterface attributes.
        /// Includes circular reference detection to prevent infinite loops.
        /// </summary>
        /// <param name="interfaceTypes">The interface types to parse</param>
        /// <param name="reportDiagnostic">Optional diagnostic reporter for circular references</param>
        /// <param name="location">Optional location for error reporting</param>
        /// <returns>Combined list of all component types from interfaces</returns>
        public static List<ITypeSymbol> ExtractAllInterfaceComponents(
            IEnumerable<ITypeSymbol> interfaceTypes,
            Action<Diagnostic>? reportDiagnostic = null,
            Location? location = null
        )
        {
            var readTypes = new List<ITypeSymbol>();
            var writeTypes = new List<ITypeSymbol>();

            ExtractInterfaceComponents(
                interfaceTypes,
                readTypes,
                writeTypes,
                reportDiagnostic,
                location
            );

            var componentTypes = new List<ITypeSymbol>();
            componentTypes.AddRange(readTypes);
            componentTypes.AddRange(writeTypes);

            return componentTypes;
        }

        /// <summary>
        /// Parses AspectInterfaceAttribute data from a type symbol.
        /// Extracts component types from IRead/IWrite interfaces and nested AspectInterface types
        /// from C# base interfaces.
        /// </summary>
        /// <param name="interfaceSymbol">The interface type symbol to parse</param>
        /// <returns>Parsed attribute data or null if not found</returns>
        public static AspectInterfaceAttributeData? ParseAspectInterfaceAttribute(
            ITypeSymbol interfaceSymbol
        )
        {
            var attribute = PerformanceCache.FindAttributeByName(
                interfaceSymbol,
                TrecsAttributeNames.AspectInterface,
                TrecsNamespaces.Trecs
            );
            if (attribute == null)
                return null;

            var readTypes = new List<ITypeSymbol>();
            var writeTypes = new List<ITypeSymbol>();
            var interfaceTypes = new List<ITypeSymbol>();

            // Extract Read/Write types from IRead<>/IWrite<> interfaces, and nested AspectInterface types
            InterfaceComponentExtractor.ExtractComponentsFromInterfaces(
                interfaceSymbol,
                readTypes,
                writeTypes,
                interfaceTypes
            );

            return new AspectInterfaceAttributeData(readTypes, writeTypes, interfaceTypes);
        }
    }
}
