using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Utility class for component type analysis and operations
    /// </summary>
    internal static class ComponentTypeHelper
    {
        /// <summary>
        /// Checks if a type is marked with the Unwrap attribute
        /// </summary>
        public static bool IsUnwrapComponent(INamedTypeSymbol type)
        {
            return ComponentAnalysisCache.IsUnwrapComponent(type);
        }

        /// <summary>
        /// Checks if a type implements IEntityComponent
        /// </summary>
        public static bool IsEntityComponent(INamedTypeSymbol type)
        {
            return ComponentAnalysisCache.IsEntityComponent(type);
        }

        /// <summary>
        /// Gets the single field from a UnwrapComponent, or null if invalid
        /// </summary>
        public static IFieldSymbol? GetUnwrapComponentField(INamedTypeSymbol type)
        {
            return ComponentAnalysisCache.GetUnwrapComponentField(type);
        }

        /// <summary>
        /// Unwraps Unwrap component types recursively to get the final type
        /// </summary>
        public static (ITypeSymbol FinalType, bool WasUnwrapped) UnwrapComponent(
            ITypeSymbol componentType,
            HashSet<ITypeSymbol>? visitedTypes = null
        )
        {
            return ComponentAnalysisCache.UnwrapComponent(componentType);
        }

        /// <summary>
        /// Validates that a UnwrapComponent has exactly one field
        /// </summary>
        public static bool ValidateUnwrapComponent(
            INamedTypeSymbol type,
            Location location,
            Action<Diagnostic> reportDiagnostic
        )
        {
            if (!IsUnwrapComponent(type))
                return true;

            // Must be a struct
            if (type.TypeKind != TypeKind.Struct)
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.UnwrapComponentMustBeStruct,
                    location,
                    type.Name
                );
                reportDiagnostic(diagnostic);
                return false;
            }

            // Must implement IEntityComponent
            if (!IsEntityComponent(type))
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.UnwrapComponentMustImplementIEntityComponent,
                    location,
                    type.Name
                );
                reportDiagnostic(diagnostic);
                return false;
            }

            // Must have exactly one field
            var field = GetUnwrapComponentField(type);
            if (field == null)
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.UnwrapComponentMustHaveExactlyOneField,
                    location,
                    type.Name
                );
                reportDiagnostic(diagnostic);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Strips a configured prefix from a name if it matches.
        /// The character following the prefix must be uppercase to avoid false positives.
        /// </summary>
        public static string StripPrefix(string name, string? prefix)
        {
            if (
                prefix != null
                && prefix.Length > 0
                && name.Length > prefix.Length
                && name.StartsWith(prefix)
                && char.IsUpper(name[prefix.Length])
            )
            {
                return name.Substring(prefix.Length);
            }
            return name;
        }

        /// <summary>
        /// Gets a property name for a component type, stripping the prefix configured
        /// on the component's declaring assembly.
        /// For nested types and generics, the prefix is stripped from individual type names
        /// before joining (e.g. Outer.CState → "OuterState", Interpolated&lt;CPos&gt; → "InterpolatedPos").
        /// </summary>
        public static string GetPropertyName(ITypeSymbol componentType)
        {
            // For generic types, always recurse into type arguments since they may
            // come from assemblies with different prefix settings
            if (componentType is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                var componentPrefix = SourceGenSettingsProvider.GetComponentPrefixForType(
                    componentType
                );
                var typeName = StripPrefix(namedType.Name, componentPrefix);
                foreach (var arg in namedType.TypeArguments)
                {
                    typeName += GetPropertyName(arg);
                }
                return typeName;
            }

            // For nested types, always resolve since the leaf type may be in
            // a different assembly with its own prefix setting
            if (componentType.ContainingType != null)
            {
                return BuildNestedName(componentType);
            }

            var prefix = SourceGenSettingsProvider.GetComponentPrefixForType(componentType);

            if (prefix == null || prefix.Length == 0)
                return componentType.Name;

            // Simple case: strip from the start
            return StripPrefix(componentType.Name, prefix);
        }

        private static string BuildNestedName(ITypeSymbol type)
        {
            var parts = new List<string>();
            var leaf = type;
            var current = type;
            while (current != null)
            {
                var name = current.Name;
                // Strip prefix from the leaf (innermost) type only -
                // containing types are typically system/wrapper classes, not component types
                if (SymbolEqualityComparer.Default.Equals(current, leaf))
                {
                    var leafPrefix = SourceGenSettingsProvider.GetComponentPrefixForType(current);
                    name = StripPrefix(name, leafPrefix);
                }
                parts.Insert(0, name);
                current = current.ContainingType;
            }
            return string.Join("", parts);
        }

        /// <summary>
        /// Returns the property name for a component type in camelCase,
        /// stripping the prefix configured on the component's declaring assembly.
        /// (e.g. with prefix "C": "CPosition" → "position").
        /// </summary>
        public static string GetCamelCasePropertyName(ITypeSymbol componentType)
        {
            var name = GetPropertyName(componentType);
            return ToCamelCase(name);
        }

        /// <summary>
        /// Gets a buffer variable name for a component type, stripping the prefix
        /// configured on the component's declaring assembly.
        /// (e.g. with prefix "C": "CPosition" → "positionBuffer").
        /// </summary>
        public static string GetComponentVariableName(ITypeSymbol componentType)
        {
            var baseName = GetPropertyName(componentType);
            return char.ToLower(baseName[0]) + baseName.Substring(1) + "Buffer";
        }

        /// <summary>
        /// Converts a PascalCase name to camelCase by lowering the first character.
        /// </summary>
        public static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// Determines the return type for a property based on component type and access pattern
        /// </summary>
        public static string GetPropertyReturnType(ITypeSymbol componentType, bool isReadOnly)
        {
            var (finalType, wasUnwrapped) = UnwrapComponent(componentType);

            if (wasUnwrapped)
            {
                // For unwrapped Unwrap component types, return by reference to the final type
                var typeString = PerformanceCache.GetDisplayString(finalType);
                return isReadOnly ? $"ref readonly {typeString}" : $"ref {typeString}";
            }
            else
            {
                // For non-unwrapped types, return by reference to the component type
                var typeString = PerformanceCache.GetDisplayString(componentType);
                return isReadOnly ? $"ref readonly {typeString}" : $"ref {typeString}";
            }
        }

        /// <summary>
        /// Gets the expression to access a component value
        /// </summary>
        public static string GetPropertyAccessExpression(
            string bufferName,
            string indexExpression,
            ITypeSymbol componentType,
            bool isReadOnly
        )
        {
            var (finalType, wasUnwrapped) = UnwrapComponent(componentType);

            var baseExpression = $"{bufferName}[{indexExpression}]";

            if (wasUnwrapped)
            {
                // Build the full unwrapping chain
                var expression = baseExpression;
                var currentType = componentType;

                while (currentType is INamedTypeSymbol namedType && IsUnwrapComponent(namedType))
                {
                    var field = GetUnwrapComponentField(namedType);
                    if (field == null)
                        break;

                    expression += $".{field.Name}";
                    currentType = field.Type;
                }

                return expression;
            }

            // For non-unwrapped types, return the base expression (ref will be added in the caller if needed)
            return baseExpression;
        }

        /// <summary>
        /// Validates a list of component types for common issues
        /// </summary>
        public static bool ValidateComponentTypes(
            IEnumerable<ITypeSymbol> componentTypes,
            Location location,
            Action<Diagnostic> reportDiagnostic
        )
        {
            var isValid = true;
            var typeNames = new HashSet<string>();

            foreach (var type in componentTypes)
            {
                // Check for duplicates
                var typeName = PerformanceCache.GetDisplayString(type);
                if (!typeNames.Add(typeName))
                {
                    var diagnostic = Diagnostic.Create(
                        DiagnosticDescriptors.DuplicateComponentType,
                        location,
                        typeName
                    );
                    reportDiagnostic(diagnostic);
                    isValid = false;
                }

                // Validate UnwrapComponents
                if (type is INamedTypeSymbol namedType)
                {
                    if (!ValidateUnwrapComponent(namedType, location, reportDiagnostic))
                    {
                        isValid = false;
                    }
                }
            }

            return isValid;
        }
    }
}
