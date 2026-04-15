using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Shared static helpers for source generators
    /// </summary>
    internal static class GeneratorBase
    {
        /// <summary>
        /// Validates that a type declaration has the partial modifier
        /// </summary>
        public static bool ValidatePartialType(
            TypeDeclarationSyntax typeDeclaration,
            string typeName,
            SourceProductionContext context
        )
        {
            if (!SymbolAnalyzer.IsPartialType(typeDeclaration))
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.AspectMustBePartial,
                    typeDeclaration.GetLocation(),
                    typeName
                );
                context.ReportDiagnostic(diagnostic);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Validates that a symbol is a struct
        /// </summary>
        public static bool ValidateStructType(
            INamedTypeSymbol symbol,
            SourceProductionContext context
        )
        {
            if (symbol.TypeKind != TypeKind.Struct)
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.AspectMustBeStruct,
                    symbol.Locations.FirstOrDefault(),
                    symbol.Name
                );
                context.ReportDiagnostic(diagnostic);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Gets all attributes of a specific type from a symbol, optionally verifying namespace
        /// </summary>
        public static IEnumerable<AttributeData> GetAttributesOfType(
            ISymbol symbol,
            string attributeName,
            string? namespaceName = null
        )
        {
            return symbol
                .GetAttributes()
                .Where(attr =>
                    attr.AttributeClass?.Name == attributeName
                    && (
                        namespaceName == null
                        || PerformanceCache.GetDisplayString(
                            attr.AttributeClass?.ContainingNamespace
                        ) == namespaceName
                    )
                );
        }

        /// <summary>
        /// Safely gets a typed constant value from an attribute argument
        /// </summary>
        public static T? GetAttributeValue<T>(
            AttributeData attributeData,
            int argumentIndex,
            T? defaultValue = default
        )
        {
            if (attributeData.ConstructorArguments.Length <= argumentIndex)
                return defaultValue;

            var argument = attributeData.ConstructorArguments[argumentIndex];
            if (argument.Value is T value)
                return value;

            return defaultValue;
        }

        /// <summary>
        /// Safely gets a named argument value from an attribute
        /// </summary>
        public static T? GetNamedArgumentValue<T>(
            AttributeData attributeData,
            string argumentName,
            T? defaultValue = default
        )
        {
            var namedArgument = attributeData.NamedArguments.FirstOrDefault(arg =>
                arg.Key == argumentName
            );

            if (namedArgument.Value.Value is T value)
                return value;

            return defaultValue;
        }

        /// <summary>
        /// Creates a safe file name for generated source
        /// </summary>
        public static string CreateSafeFileName(INamedTypeSymbol symbol, string suffix)
        {
            return SymbolAnalyzer.GetSafeFileName(symbol, suffix);
        }

        /// <summary>
        /// Gets the accessibility level of a symbol as a string
        /// </summary>
        public static string GetAccessibilityString(ISymbol symbol)
        {
            return SymbolAnalyzer.GetAccessibilityModifier(symbol);
        }

        /// <summary>
        /// Reports a diagnostic and logs the error
        /// </summary>
        public static void ReportError(
            SourceProductionContext context,
            DiagnosticDescriptor descriptor,
            Location location,
            params object[] messageArgs
        )
        {
            var diagnostic = Diagnostic.Create(descriptor, location, messageArgs);
            context.ReportDiagnostic(diagnostic);

            var message = string.Format(descriptor.MessageFormat.ToString(), messageArgs);
            SourceGenLogger.Log($"[ERROR] {message}");
        }

        /// <summary>
        /// Creates a production context action that handles exceptions gracefully
        /// </summary>
        public static Action<SourceProductionContext, T> CreateSafeProductionAction<T>(
            Action<SourceProductionContext, T> action,
            string generatorName
        )
        {
            return (context, input) =>
            {
                try
                {
                    action(context, input);
                }
                catch (Exception ex)
                {
                    SourceGenLogger.Log($"[{generatorName}] Unhandled exception: {ex.Message}");
                    SourceGenLogger.Log($"[{generatorName}] Stack trace: {ex.StackTrace}");

                    var diagnostic = Diagnostic.Create(
                        DiagnosticDescriptors.UnhandledSourceGenError,
                        Location.None,
                        ex.Message
                    );
                    context.ReportDiagnostic(diagnostic);
                }
            };
        }
    }
}
