using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Template
{
    /// <summary>
    /// Compile-time validation for template class declarations
    /// </summary>
    internal static class TemplateValidator
    {
        /// <summary>
        /// Validates a template declaration and reports diagnostics for any issues
        /// </summary>
        public static bool Validate(
            TypeDeclarationSyntax declaration,
            INamedTypeSymbol symbol,
            TemplateDefinitionData data,
            Action<Diagnostic> reportDiagnostic
        )
        {
            bool isValid = true;

            // Must be partial
            if (!SymbolAnalyzer.IsPartialType(declaration))
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TemplateMustBePartial,
                        declaration.GetLocation(),
                        symbol.Name
                    )
                );
                isValid = false;
            }

            // Must be class
            if (symbol.TypeKind != TypeKind.Class)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TemplateMustBeClass,
                        declaration.GetLocation(),
                        symbol.Name
                    )
                );
                isValid = false;
            }

            // Validate component field attribute combinations
            foreach (var component in data.Components)
            {
                if (
                    !ValidateComponentAttributes(
                        component,
                        declaration.GetLocation(),
                        reportDiagnostic,
                        symbol.Name
                    )
                )
                {
                    isValid = false;
                }
            }

            // Validate instance fields
            foreach (var member in symbol.GetMembers())
            {
                if (member is IFieldSymbol field && !field.IsStatic && !field.IsConst)
                {
                    // Must be public
                    if (field.DeclaredAccessibility != Accessibility.Public)
                    {
                        reportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.TemplateFieldMustBePublic,
                                field.Locations.FirstOrDefault() ?? declaration.GetLocation(),
                                field.Name,
                                symbol.Name
                            )
                        );
                        isValid = false;
                    }

                    // Must implement IEntityComponent
                    if (!ImplementsIEntityComponent(field.Type))
                    {
                        reportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.TemplateFieldMustBeEntityComponent,
                                field.Locations.FirstOrDefault() ?? declaration.GetLocation(),
                                field.Name,
                                PerformanceCache.GetDisplayString(field.Type),
                                symbol.Name
                            )
                        );
                        isValid = false;
                    }
                    // Check for managed fields in component types
                    else if (field.Type is INamedTypeSymbol componentType)
                    {
                        foreach (var componentMember in componentType.GetMembers())
                        {
                            if (
                                componentMember is IFieldSymbol componentField
                                && !componentField.IsStatic
                                && !componentField.IsConst
                                && componentField.Type.IsReferenceType
                            )
                            {
                                reportDiagnostic(
                                    Diagnostic.Create(
                                        DiagnosticDescriptors.ComponentHasManagedFields,
                                        field.Locations.FirstOrDefault()
                                            ?? declaration.GetLocation(),
                                        PerformanceCache.GetDisplayString(componentType),
                                        field.Name,
                                        symbol.Name,
                                        componentField.Name,
                                        PerformanceCache.GetDisplayString(componentField.Type)
                                    )
                                );
                                isValid = false;
                            }
                        }
                    }
                }
            }

            // Globals templates must have explicit defaults on all fields
            if (data.IsGlobals)
            {
                foreach (var component in data.Components)
                {
                    if (!component.HasExplicitDefault)
                    {
                        reportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.GlobalsTemplateFieldMustHaveDefault,
                                declaration.GetLocation(),
                                component.FieldName,
                                symbol.Name
                            )
                        );
                        isValid = false;
                    }
                }
            }

            return isValid;
        }

        private static bool ValidateComponentAttributes(
            TemplateComponentData component,
            Location location,
            Action<Diagnostic> reportDiagnostic,
            string templateName
        )
        {
            bool isValid = true;

            // [Interpolated] + [Constant]
            if (component.IsInterpolated && component.IsConstant)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TemplateInvalidAttributeCombination,
                        location,
                        component.FieldName,
                        templateName,
                        "Interpolated",
                        "Constant"
                    )
                );
                isValid = false;
            }

            // [Interpolated] + [FixedUpdateOnly]
            if (component.IsInterpolated && component.IsFixedUpdateOnly)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TemplateInvalidAttributeCombination,
                        location,
                        component.FieldName,
                        templateName,
                        "Interpolated",
                        "FixedUpdateOnly"
                    )
                );
                isValid = false;
            }

            // [Interpolated] + [VariableUpdateOnly]
            if (component.IsInterpolated && component.IsVariableUpdateOnly)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TemplateInvalidAttributeCombination,
                        location,
                        component.FieldName,
                        templateName,
                        "Interpolated",
                        "VariableUpdateOnly"
                    )
                );
                isValid = false;
            }

            // [Input] + [Constant]
            if (component.IsInput && component.IsConstant)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TemplateInvalidAttributeCombination,
                        location,
                        component.FieldName,
                        templateName,
                        "Input",
                        "Constant"
                    )
                );
                isValid = false;
            }

            // [Input] + [VariableUpdateOnly]
            if (component.IsInput && component.IsVariableUpdateOnly)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TemplateInvalidAttributeCombination,
                        location,
                        component.FieldName,
                        templateName,
                        "Input",
                        "VariableUpdateOnly"
                    )
                );
                isValid = false;
            }

            // [FixedUpdateOnly] + [VariableUpdateOnly]
            if (component.IsFixedUpdateOnly && component.IsVariableUpdateOnly)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TemplateInvalidAttributeCombination,
                        location,
                        component.FieldName,
                        templateName,
                        "FixedUpdateOnly",
                        "VariableUpdateOnly"
                    )
                );
                isValid = false;
            }

            return isValid;
        }

        private static bool ImplementsIEntityComponent(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol namedType)
            {
                return namedType.AllInterfaces.Any(i =>
                    i.Name == "IEntityComponent"
                    && SymbolAnalyzer.IsInNamespace(i.ContainingNamespace, "Trecs")
                );
            }
            return false;
        }
    }
}
