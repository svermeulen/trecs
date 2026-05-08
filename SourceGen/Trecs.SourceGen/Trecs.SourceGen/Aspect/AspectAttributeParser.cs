using System.Collections.Generic;
using System.Collections.Immutable;
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
        public static AspectAttributeData ParseAspectData(INamedTypeSymbol symbol)
        {
            var readTypes = new List<ITypeSymbol>();
            var writeTypes = new List<ITypeSymbol>();
            var interfaceTypes = new List<ITypeSymbol>();

            // Extract Read/Write types from IRead<>/IWrite<> interfaces
            // and aspect-interface types (base interfaces that extend Trecs.IAspect) from
            // the base interface list.
            // Note: ITagged, IInSet, IWithoutTags, IWithoutComponents are NOT extracted
            // for aspects — filtering is specified at iteration sites instead.
            InterfaceComponentExtractor.ExtractComponentsFromInterfaces(
                symbol,
                readTypes,
                writeTypes,
                interfaceTypes
            );

            // Recursively extract components from nested aspect interfaces
            AspectInterfaceParser.ExtractInterfaceComponents(interfaceTypes, readTypes, writeTypes);

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
    /// Data class containing parsed aspect-interface information (components declared directly
    /// on an aspect-interface symbol, plus any nested aspect interfaces it extends).
    /// </summary>
    internal class AspectInterfaceData
    {
        public ImmutableArray<ITypeSymbol> ReadTypes { get; }
        public ImmutableArray<ITypeSymbol> WriteTypes { get; }
        public ImmutableArray<ITypeSymbol> InterfaceTypes { get; }

        public AspectInterfaceData(
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
