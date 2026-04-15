using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Provides shared assembly filtering functionality for all Trecs source generators
    /// to improve compilation performance by skipping assemblies that don't reference Trecs
    /// </summary>
    internal static class AssemblyFilterHelper
    {
        /// <summary>
        /// Creates an incremental value provider that indicates whether the compilation references the Trecs assembly
        /// </summary>
        public static IncrementalValueProvider<bool> CreateTrecsReferenceCheck(
            IncrementalGeneratorInitializationContext context
        )
        {
            return context.CompilationProvider.Select(
                static (compilation, _) => HasTrecsReference(compilation)
            );
        }

        /// <summary>
        /// Filters a syntax provider to only process when Trecs is referenced
        /// </summary>
        public static IncrementalValuesProvider<T> FilterByTrecsReference<T>(
            IncrementalValuesProvider<T> provider,
            IncrementalValueProvider<bool> hasTrecsReference
        )
        {
            return provider
                .Combine(hasTrecsReference)
                .Where(static combined => combined.Right) // Only process if Trecs is referenced
                .Select(static (combined, _) => combined.Left);
        }

        private static bool HasTrecsReference(Compilation compilation)
        {
            // Allow the Trecs assembly itself
            if (
                compilation.AssemblyName?.Equals("Trecs", StringComparison.OrdinalIgnoreCase)
                == true
            )
                return true;

            // Allow assemblies that reference Trecs
            return compilation.ReferencedAssemblyNames.Any(assemblyName =>
                assemblyName.Name.Equals("Trecs", StringComparison.OrdinalIgnoreCase)
            );
        }
    }
}
