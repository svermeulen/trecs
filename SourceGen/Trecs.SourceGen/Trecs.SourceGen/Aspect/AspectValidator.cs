using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Aspect
{
    /// <summary>
    /// Handles validation of Aspect types and their configurations
    /// </summary>
    internal static class AspectValidator
    {
        /// <summary>
        /// Validates an Aspect struct for all requirements
        /// </summary>
        public static bool ValidateAspect(
            TypeDeclarationSyntax declaration,
            INamedTypeSymbol symbol,
            AspectAttributeData attributeData,
            Action<Diagnostic> reportDiagnostic
        )
        {
            bool isValid = true;

            // Must be partial
            if (!SymbolAnalyzer.IsPartialType(declaration))
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.AspectMustBePartial,
                    declaration.GetLocation(),
                    symbol.Name
                );
                reportDiagnostic(diagnostic);
                isValid = false;
            }

            // Must be struct
            if (symbol.TypeKind != TypeKind.Struct)
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.AspectMustBeStruct,
                    declaration.GetLocation(),
                    symbol.Name
                );
                reportDiagnostic(diagnostic);
                isValid = false;
            }

            // Validate component types
            var allComponentTypes = attributeData.ReadTypes.Concat(attributeData.WriteTypes);
            if (
                !ComponentTypeHelper.ValidateComponentTypes(
                    allComponentTypes,
                    declaration.GetLocation(),
                    reportDiagnostic
                )
            )
            {
                isValid = false;
            }

            // Validate AspectInterface references
            if (
                !ValidateAspectInterfaces(
                    attributeData,
                    declaration.GetLocation(),
                    reportDiagnostic
                )
            )
            {
                isValid = false;
            }

            return isValid;
        }

        /// <summary>
        /// Validates all Unwrap component types in a compilation
        /// </summary>
        public static void ValidateAllUnwrapComponents(
            TypeDeclarationSyntax[] unwrapComponentTypes,
            Compilation compilation,
            Action<Diagnostic> reportDiagnostic
        )
        {
            foreach (var structDecl in unwrapComponentTypes)
            {
                var semanticModel = compilation.GetSemanticModel(structDecl.SyntaxTree);
                var symbol = semanticModel.GetDeclaredSymbol(structDecl);

                if (symbol is INamedTypeSymbol namedSymbol)
                {
                    ComponentTypeHelper.ValidateUnwrapComponent(
                        namedSymbol,
                        structDecl.GetLocation(),
                        reportDiagnostic
                    );
                }
            }
        }

        /// <summary>
        /// Validates Aspect usage patterns and emits warnings/errors for issues that
        /// don't gate aspect generation but should still be surfaced to the user. The
        /// caller does NOT use the return value to decide whether to skip generation,
        /// so any Error severity diagnostics emitted here will fail the build at the
        /// Roslyn level while still letting the rest of the aspect type generate.
        /// </summary>
        public static void ValidateUsagePatterns(
            AspectAttributeData attributeData,
            INamedTypeSymbol symbol,
            Location location,
            Action<Diagnostic> reportDiagnostic
        )
        {
            // Warn if Aspect has no components at all
            if (!attributeData.ReadTypes.Any() && !attributeData.WriteTypes.Any())
            {
                // This could be intentional for tag-only filtering, so just log it
                SourceGenLogger.Log(
                    $"[AspectValidator] Aspect {symbol.Name} has no component types defined"
                );
            }

            // Check for common naming issues
            if (symbol.Name.EndsWith("Aspect") && symbol.Name.Length > "Aspect".Length)
            {
                // This is good practice, no warning needed
            }
            else if (!symbol.Name.EndsWith("View"))
            {
                SourceGenLogger.Log(
                    $"[AspectValidator] Aspect {symbol.Name} doesn't follow naming convention (should end with 'View' or 'Aspect')"
                );
            }
        }

        /// <summary>
        /// Validates AspectInterface references for an Aspect
        /// </summary>
        public static bool ValidateAspectInterfaces(
            AspectAttributeData attributeData,
            Location location,
            Action<Diagnostic> reportDiagnostic
        )
        {
            bool isValid = true;
            var visitedInterfaces = new HashSet<string>();

            foreach (var interfaceType in attributeData.InterfaceTypes)
            {
                if (
                    !ValidateAspectInterface(
                        interfaceType,
                        location,
                        reportDiagnostic,
                        visitedInterfaces
                    )
                )
                {
                    isValid = false;
                }
            }

            return isValid;
        }

        /// <summary>
        /// Validates a single AspectInterface with circular reference detection
        /// </summary>
        private static bool ValidateAspectInterface(
            ITypeSymbol interfaceType,
            Location location,
            Action<Diagnostic> reportDiagnostic,
            HashSet<string> visitedInterfaces
        )
        {
            bool isValid = true;
            var interfaceName = PerformanceCache.GetDisplayString(interfaceType);

            // Check for circular references
            if (visitedInterfaces.Contains(interfaceName))
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.CircularAspectInterfaceReference,
                    location,
                    string.Join(" -> ", visitedInterfaces) + " -> " + interfaceName
                );
                reportDiagnostic(diagnostic);
                return false;
            }

            // Add to visited set for circular reference detection
            visitedInterfaces.Add(interfaceName);

            try
            {
                // Validate that it's actually an interface
                if (interfaceType.TypeKind != TypeKind.Interface)
                {
                    var diagnostic = Diagnostic.Create(
                        DiagnosticDescriptors.AspectInterfaceMustBeInterface,
                        location,
                        interfaceName,
                        interfaceType.TypeKind.ToString().ToLowerInvariant()
                    );
                    reportDiagnostic(diagnostic);
                    isValid = false;
                }

                // Validate that it has AspectInterfaceAttribute
                var hasAspectInterfaceAttribute = PerformanceCache.HasAttributeByName(
                    interfaceType,
                    TrecsAttributeNames.AspectInterface,
                    TrecsNamespaces.Trecs
                );

                if (!hasAspectInterfaceAttribute)
                {
                    var diagnostic = Diagnostic.Create(
                        DiagnosticDescriptors.AspectInterfaceNotFound,
                        location,
                        interfaceName
                    );
                    reportDiagnostic(diagnostic);
                    isValid = false;
                }

                // Nested interface references are validated via circular reference detection in AspectInterfaceParser
            }
            finally
            {
                // Remove from visited set to allow the same interface to be used in different hierarchies
                visitedInterfaces.Remove(interfaceName);
            }

            return isValid;
        }
    }
}
