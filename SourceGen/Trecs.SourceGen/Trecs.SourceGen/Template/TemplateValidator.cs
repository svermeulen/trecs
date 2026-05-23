using System;
using System.Collections.Generic;
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
        /// Validates a template declaration and reports diagnostics for any issues. Runs
        /// inside the pipeline transform stage so it produces <see cref="DiagnosticInfo"/>
        /// (value-equatable) rather than <see cref="Diagnostic"/> (reference-equatable,
        /// breaks cache). The terminal stage materializes real diagnostics.
        /// </summary>
        public static bool Validate(
            TypeDeclarationSyntax declaration,
            INamedTypeSymbol symbol,
            TemplateDefinitionData data,
            Action<DiagnosticInfo> reportDiagnostic
        )
        {
            bool isValid = true;
            var declLocation = LocationInfo.From(declaration.GetLocation());

            // Must be partial
            if (!SymbolAnalyzer.IsPartialType(declaration))
            {
                reportDiagnostic(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.TemplateMustBePartial,
                        declLocation,
                        symbol.Name
                    )
                );
                isValid = false;
            }

            // Must be class
            if (symbol.TypeKind != TypeKind.Class)
            {
                reportDiagnostic(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.TemplateMustBeClass,
                        declLocation,
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
                        declLocation,
                        reportDiagnostic,
                        symbol.Name
                    )
                )
                {
                    isValid = false;
                }
            }

            // Build a lookup from template-field name to its [Input] behaviour so the
            // per-field walk below can apply input-specific component checks without
            // re-parsing the attribute. Value is the "Retain" suffix of the enum value
            // (or empty string) so it's cheap to compare; absence from the dict means
            // the template field is not [Input].
            Dictionary<string, string>? inputFieldBehaviours = null;
            foreach (var c in data.Components)
            {
                if (!c.IsInput)
                    continue;
                inputFieldBehaviours ??= new Dictionary<string, string>();
                inputFieldBehaviours[c.FieldName] = c.OnMissing ?? string.Empty;
            }

            // Validate instance fields
            foreach (var member in symbol.GetMembers())
            {
                if (member is IFieldSymbol field && !field.IsStatic && !field.IsConst)
                {
                    var fieldLocation = LocationInfo.From(
                        field.Locations.FirstOrDefault() ?? declaration.GetLocation()
                    );

                    // Must have no access modifier — template fields are a
                    // config DSL, not an API surface. The check is
                    // syntax-level because Roslyn reports both `private T X;`
                    // and bare `T X;` as Accessibility.Private.
                    if (HasExplicitAccessModifier(field))
                    {
                        reportDiagnostic(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.TemplateFieldMustHaveNoAccessModifier,
                                fieldLocation,
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
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.TemplateFieldMustBeEntityComponent,
                                fieldLocation,
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
                        bool isInputField =
                            inputFieldBehaviours != null
                            && inputFieldBehaviours.TryGetValue(field.Name, out _);
                        bool isRetainField =
                            isInputField && inputFieldBehaviours![field.Name].EndsWith(".Retain");

                        foreach (var componentMember in componentType.GetMembers())
                        {
                            if (
                                componentMember is not IFieldSymbol componentField
                                || componentField.IsStatic
                                || componentField.IsConst
                            )
                                continue;

                            if (componentField.Type.IsReferenceType)
                            {
                                reportDiagnostic(
                                    DiagnosticInfo.Create(
                                        DiagnosticDescriptors.ComponentHasManagedFields,
                                        fieldLocation,
                                        PerformanceCache.GetDisplayString(componentType),
                                        field.Name,
                                        symbol.Name,
                                        componentField.Name,
                                        PerformanceCache.GetDisplayString(componentField.Type)
                                    )
                                );
                                isValid = false;
                            }

                            if (isInputField)
                            {
                                if (
                                    !ValidateInputComponentFieldType(
                                        componentField,
                                        componentType,
                                        field,
                                        symbol.Name,
                                        isRetainField,
                                        declLocation,
                                        reportDiagnostic
                                    )
                                )
                                {
                                    isValid = false;
                                }
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
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.GlobalsTemplateFieldMustHaveDefault,
                                declLocation,
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
            CheckPartitionCount(data, declLocation, reportDiagnostic);

            return isValid;
        }

        // Threshold above which we emit TRECS038. Chosen so that up to 3 binary
        // dims (8 partitions) and a 4-way + 2 binary mix (16) are silent — those
        // shapes are common and reasonable. Above 16 the design is more clearly
        // opting into a lot of groups and deserves a nudge toward sets.
        const int PartitionWarningThreshold = 16;

        private static void CheckPartitionCount(
            TemplateDefinitionData data,
            LocationInfo declLocation,
            Action<DiagnosticInfo> reportDiagnostic
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
                DiagnosticInfo.Create(
                    DiagnosticDescriptors.TemplatePartitionCountHigh,
                    declLocation,
                    data.TypeName,
                    count.ToString(),
                    data.Dimensions.Length.ToString(),
                    dimShape
                )
            );
        }

        private static bool ValidateComponentAttributes(
            TemplateComponentData component,
            LocationInfo location,
            Action<DiagnosticInfo> reportDiagnostic,
            string templateName
        )
        {
            bool isValid = true;

            // [Interpolated] + [Constant]
            if (component.IsInterpolated && component.IsConstant)
            {
                reportDiagnostic(
                    DiagnosticInfo.Create(
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
                    DiagnosticInfo.Create(
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
                    DiagnosticInfo.Create(
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
                    DiagnosticInfo.Create(
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

        // Persistent-pointer struct types: allocating one from an input system would
        // outlive the input frame (which is bulk-released) and leak. Mapped to the
        // Input*-equivalent in the diagnostic message so the suggested fix is concrete.
        private static readonly (string PersistentName, string InputName)[] PersistentPtrTypes =
        {
            ("NativeUniquePtr", "InputNativeUniquePtr"),
            ("NativeSharedPtr", "InputNativeSharedPtr"),
            ("UniquePtr", "InputUniquePtr"),
            ("SharedPtr", "InputSharedPtr"),
        };

        // Input-pointer types that themselves carry a handle into the per-frame arena
        // / refcount slot. Safe in Reset-mode input components; never safe in
        // Retain-mode because the previous frame's slot has already been released by
        // the time Retain would re-apply the component.
        private static readonly string[] InputPtrTypeNames =
        {
            "InputNativeUniquePtr",
            "InputNativeSharedPtr",
            "InputUniquePtr",
            "InputSharedPtr",
        };

        private static bool ValidateInputComponentFieldType(
            IFieldSymbol componentField,
            INamedTypeSymbol componentType,
            IFieldSymbol templateField,
            string templateName,
            bool isRetain,
            LocationInfo fallbackLocation,
            Action<DiagnosticInfo> reportDiagnostic
        )
        {
            if (componentField.Type is not INamedTypeSymbol named)
                return true;
            if (!SymbolAnalyzer.IsInNamespace(named.ContainingNamespace, TrecsNamespaces.Trecs))
                return true;

            var fieldLoc = componentField.Locations.FirstOrDefault();
            var location = fieldLoc != null ? LocationInfo.From(fieldLoc) : fallbackLocation;
            bool isValid = true;

            // TRECS121: persistent ptr in [Input] component.
            foreach (var (persistentName, inputName) in PersistentPtrTypes)
            {
                if (named.Name == persistentName && named.TypeArguments.Length == 1)
                {
                    reportDiagnostic(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.InputComponentHasPersistentPtrField,
                            location,
                            componentField.Name,
                            PerformanceCache.GetDisplayString(componentType),
                            templateField.Name,
                            PerformanceCache.GetDisplayString(named),
                            $"{inputName}<{PerformanceCache.GetDisplayString(named.TypeArguments[0])}>"
                        )
                    );
                    return false;
                }
            }

            // TRECS122: TrecsList<T> in [Input] component.
            if (named.Name == "TrecsList" && named.TypeArguments.Length == 1)
            {
                reportDiagnostic(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InputComponentHasTrecsListField,
                        location,
                        componentField.Name,
                        PerformanceCache.GetDisplayString(componentType),
                        templateField.Name,
                        PerformanceCache.GetDisplayString(named.TypeArguments[0])
                    )
                );
                return false;
            }

            // TRECS123: InputXxxPtr in a [Input(MissingInputBehavior.Retain)] component.
            if (
                isRetain
                && InputPtrTypeNames.Contains(named.Name)
                && named.TypeArguments.Length == 1
            )
            {
                reportDiagnostic(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InputRetainWithInputPtrField,
                        location,
                        PerformanceCache.GetDisplayString(componentType),
                        templateField.Name,
                        componentField.Name,
                        PerformanceCache.GetDisplayString(named)
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
