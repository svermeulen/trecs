using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Aspect
{
    /// <summary>
    /// Handles parsing Aspect data from symbols implementing IAspect
    /// </summary>
    internal static class AspectAttributeParser
    {
        /// <summary>
        /// Parses aspect data from a symbol implementing IAspect
        /// </summary>
        public static AspectAttributeData? ParseAspectData(
            INamedTypeSymbol symbol,
            Action<Diagnostic>? reportDiagnostic = null,
            Location? location = null
        )
        {
            var readTypes = new List<ITypeSymbol>();
            var writeTypes = new List<ITypeSymbol>();
            var interfaceTypes = new List<ITypeSymbol>();

            // Extract Read/Write types from IRead<>/IWrite<> interfaces
            // and AspectInterface types from base interfaces.
            // Note: IHasTags, IInSet, IWithoutTags, IWithoutComponents are NOT extracted
            // for aspects — filtering is specified at iteration sites instead.
            InterfaceComponentExtractor.ExtractComponentsFromInterfaces(
                symbol,
                readTypes,
                writeTypes,
                interfaceTypes
            );

            // Recursively extract components from AspectInterface types
            AspectInterfaceParser.ExtractInterfaceComponents(
                interfaceTypes,
                readTypes,
                writeTypes,
                reportDiagnostic,
                location
            );

            // Remove duplicates using optimized cache
            var distinctReadTypes = PerformanceCache.GetDistinctTypes(readTypes);
            var distinctWriteTypes = PerformanceCache.GetDistinctTypes(writeTypes);

            return new AspectAttributeData(
                distinctReadTypes,
                distinctWriteTypes,
                interfaceTypes.ToImmutableArray()
            );
        }
    }

    /// <summary>
    /// Data class containing parsed Aspect information
    /// </summary>
    internal class AspectAttributeData
    {
        public ImmutableArray<ITypeSymbol> ReadTypes { get; }
        public ImmutableArray<ITypeSymbol> WriteTypes { get; }
        public ImmutableArray<ITypeSymbol> InterfaceTypes { get; }

        private ImmutableArray<ITypeSymbol> _allComponentTypes;
        public ImmutableArray<ITypeSymbol> AllComponentTypes
        {
            get
            {
                if (_allComponentTypes.IsDefault)
                {
                    _allComponentTypes = Performance.PerformanceCache.MergeDistinctTypes(
                        ReadTypes,
                        WriteTypes
                    );
                }
                return _allComponentTypes;
            }
        }

        public AspectAttributeData(
            ImmutableArray<ITypeSymbol> readTypes,
            ImmutableArray<ITypeSymbol> writeTypes,
            ImmutableArray<ITypeSymbol> interfaceTypes = default
        )
        {
            ReadTypes = readTypes.IsDefault ? ImmutableArray<ITypeSymbol>.Empty : readTypes;
            WriteTypes = writeTypes.IsDefault ? ImmutableArray<ITypeSymbol>.Empty : writeTypes;
            InterfaceTypes = interfaceTypes.IsDefault
                ? ImmutableArray<ITypeSymbol>.Empty
                : interfaceTypes;
        }
    }

    /// <summary>
    /// Data class containing parsed AspectInterface attribute information
    /// </summary>
    internal class AspectInterfaceAttributeData
    {
        public ImmutableArray<ITypeSymbol> ReadTypes { get; }
        public ImmutableArray<ITypeSymbol> WriteTypes { get; }
        public ImmutableArray<ITypeSymbol> InterfaceTypes { get; }

        public AspectInterfaceAttributeData(
            List<ITypeSymbol> readTypes,
            List<ITypeSymbol> writeTypes,
            List<ITypeSymbol>? interfaceTypes = null
        )
        {
            ReadTypes = readTypes?.ToImmutableArray() ?? ImmutableArray<ITypeSymbol>.Empty;
            WriteTypes = writeTypes?.ToImmutableArray() ?? ImmutableArray<ITypeSymbol>.Empty;
            InterfaceTypes =
                interfaceTypes?.ToImmutableArray() ?? ImmutableArray<ITypeSymbol>.Empty;
        }
    }
}
