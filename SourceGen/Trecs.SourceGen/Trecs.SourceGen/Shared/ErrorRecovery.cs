#nullable enable

using System;
using Microsoft.CodeAnalysis;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Provides error recovery and fallback generation for source generators
    /// </summary>
    internal static class ErrorRecovery
    {
        /// <summary>
        /// Generates a partial class with error comments when generation fails
        /// </summary>
        public static string GenerateErrorFallback(
            string typeName,
            string namespaceName,
            string errorMessage,
            string typeKind = "class"
        )
        {
            var sanitizedError = errorMessage
                .Replace("*/", "* /")
                .Replace("\n", " ")
                .Replace("\r", " ");

            return $@"#nullable enable
// This file was generated with errors. Please check the diagnostics for details.

{(string.IsNullOrEmpty(namespaceName) ? "" : $"namespace {namespaceName}\n{{\n")}    public partial {typeKind} {typeName}
    {{
        /*
         * ERROR: Source generation failed
         *
         * {sanitizedError}
         *
         * Please fix the errors and rebuild the project.
         */
    }}
{(string.IsNullOrEmpty(namespaceName) ? "" : "}\n")}";
        }

        /// <summary>
        /// Safely executes a generation step with error recovery
        /// </summary>
        public static bool TryExecute(
            Action action,
            SourceProductionContext context,
            Location location,
            string operationName
        )
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                ReportError(context, location, operationName, ex);
                return false;
            }
        }

        /// <summary>
        /// Safely executes a generation step that returns a value with error recovery
        /// </summary>
        public static T? TryExecute<T>(
            Func<T> func,
            SourceProductionContext context,
            Location location,
            string operationName
        )
            where T : class
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                ReportError(context, location, operationName, ex);
                return null;
            }
        }

        /// <summary>
        /// Safely executes a generation step that returns a bool with error recovery
        /// </summary>
        public static bool? TryExecuteBool(
            Func<bool> func,
            SourceProductionContext context,
            Location location,
            string operationName
        )
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                ReportError(context, location, operationName, ex);
                return null;
            }
        }

        /// <summary>
        /// Reports an error with consistent formatting
        /// </summary>
        public static void ReportError(
            SourceProductionContext context,
            Location location,
            string operationName,
            Exception ex
        )
        {
            var diagnostic = Diagnostic.Create(
                DiagnosticDescriptors.SourceGenerationError,
                location,
                operationName,
                ex.Message
            );

            context.ReportDiagnostic(diagnostic);

            // Log detailed error for debugging
            SourceGenLogger.Log($"[ERROR] {operationName} failed: {ex}");
        }

        /// <summary>
        /// Validates input and reports appropriate diagnostics
        /// </summary>
        public static bool ValidateNotNull<T>(
            T? value,
            SourceProductionContext context,
            Location location,
            string itemName
        )
            where T : class
        {
            if (value == null)
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.CouldNotResolveSymbol,
                    location,
                    itemName
                );
                context.ReportDiagnostic(diagnostic);
                return false;
            }
            return true;
        }
    }
}
