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
    }
}
