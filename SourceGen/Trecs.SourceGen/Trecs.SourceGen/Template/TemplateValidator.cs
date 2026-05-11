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
                    // Must have no access modifier — template fields are a
                    // config DSL, not an API surface. The check is
                    // syntax-level because Roslyn reports both `private T X;`
                    // and bare `T X;` as Accessibility.Private.
                    if (HasExplicitAccessModifier(field))
                    {
                        reportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.TemplateFieldMustHaveNoAccessModifier,
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
                    // Check for managed fields in component types. This is
                    // intentionally one level deep — we only care whether the
                    // component itself contains reference fields. Nested
                    // value-type chains can't introduce cycles here because
                    // this loop doesn't recurse into them.
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

            // Warn when a template's cross-product partition count crosses the
            // threshold where sets are usually the better tool. Each emitted
            // partition pre-allocates a contiguous component buffer, so the
            // memory and startup cost compounds quickly with dimensions.
            CheckPartitionCount(data, declaration, reportDiagnostic);

            return isValid;
        }

        // Threshold above which we emit TRECS038. Chosen so that up to 3 binary
        // dims (8 partitions) and a 4-way + 2 binary mix (16) are silent — those
        // shapes are common and reasonable. Above 16 the design is more clearly
        // opting into a lot of groups and deserves a nudge toward sets.
        const int PartitionWarningThreshold = 16;

        private static void CheckPartitionCount(
            TemplateDefinitionData data,
            TypeDeclarationSyntax declaration,
            Action<Diagnostic> reportDiagnostic
        )
        {
            if (data.Dimensions.Length == 0)
                return;

            // Cross-product size: arity-1 dims contribute 2 (present + absent);
            // multi-variant dims contribute the variant count. Multiply across
            // dims using long arithmetic — even pathological inputs stay well
            // below long.MaxValue (and if they don't, the user has bigger
            // problems than this warning).
            long count = 1;
            foreach (var dim in data.Dimensions)
            {
                int dimSize = dim.IsPresenceAbsence ? 2 : dim.VariantTagTypeNames.Length;
                count *= dimSize;
            }

            if (count <= PartitionWarningThreshold)
                return;

            var dimShape = string.Join(
                " × ",
                data.Dimensions.Select(d =>
                    d.IsPresenceAbsence
                        ? "2 (presence/absence)"
                        : d.VariantTagTypeNames.Length.ToString()
                )
            );

            reportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.TemplatePartitionCountHigh,
                    declaration.GetLocation(),
                    data.TypeName,
                    count,
                    data.Dimensions.Length,
                    dimShape
                )
            );
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

        private static bool HasExplicitAccessModifier(IFieldSymbol field)
        {
            foreach (var syntaxRef in field.DeclaringSyntaxReferences)
            {
                if (
                    syntaxRef.GetSyntax() is VariableDeclaratorSyntax declarator
                    && declarator.Parent?.Parent is FieldDeclarationSyntax fieldDecl
                )
                {
                    foreach (var modifier in fieldDecl.Modifiers)
                    {
                        switch (modifier.RawKind)
                        {
                            case (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword:
                            case (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword:
                            case (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.InternalKeyword:
                            case (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.ProtectedKeyword:
                                return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
