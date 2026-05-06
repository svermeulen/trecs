using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

            return isValid;
        }

        /// <summary>
        /// Validates that an interface declaring IAspect is marked partial — otherwise the
        /// source generator cannot attach the generated ref-returning property contract.
        /// Returns true if valid.
        /// </summary>
        public static bool ValidateAspectInterfaceDeclaration(
            InterfaceDeclarationSyntax declaration,
            INamedTypeSymbol symbol,
            Action<Diagnostic> reportDiagnostic
        )
        {
            if (!SymbolAnalyzer.IsPartialType(declaration))
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.AspectInterfaceMustBePartial,
                    declaration.GetLocation(),
                    symbol.Name
                );
                reportDiagnostic(diagnostic);
                return false;
            }

            return true;
        }
    }
}
