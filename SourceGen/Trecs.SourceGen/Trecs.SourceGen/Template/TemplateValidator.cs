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
                    // Apply the component field-type rules (managed-field, TRECS121-123,
                    // TRECS136-137). The walk recurses into nested plain value-type
                    // structs so a forbidden type buried inside a sub-struct can't escape
                    // the rules — diagnostics name the full field path (e.g. "Nested.Ptr").
                    else if (field.Type is INamedTypeSymbol componentType)
                    {
                        bool isInputField =
                            inputFieldBehaviours != null
                            && inputFieldBehaviours.TryGetValue(field.Name, out _);
                        bool isRetainField =
                            isInputField && inputFieldBehaviours![field.Name].EndsWith(".Retain");

                        if (
                            !WalkComponentFields(
                                componentType,
                                componentType,
                                field,
                                symbol.Name,
                                isInputField,
                                isRetainField,
                                fieldPath: null,
                                fieldLocation,
                                declLocation,
                                reportDiagnostic,
                                visited: new HashSet<INamedTypeSymbol>(
                                    SymbolEqualityComparer.Default
                                )
                            )
                        )
                        {
                            isValid = false;
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

        // Recursively applies the component field-type rules (managed-field, TRECS121-123,
        // TRECS136-137) to every field of <paramref name="currentType"/>, descending into
        // nested plain value-type structs so a forbidden type buried below the top level
        // cannot escape. <paramref name="fieldPath"/> is the dotted path from the component
        // root down to (but not including) the current type's fields — null at the top
        // level — and is prefixed onto each field name in the diagnostics so the offending
        // field is findable (e.g. "Nested.Ptr").
        //
        // Recursion only follows value-type fields: reference-type fields are caught by the
        // managed-field check at level one (the component itself can't be a reference type),
        // and any reference-type sub-field is reported as managed and not descended into, so
        // the walk can't cycle through classes. Value-type cycles are impossible in C#
        // (the compiler rejects a struct that contains itself), but a visited-set guards
        // against re-walking shared sub-structs anyway.
        private static bool WalkComponentFields(
            INamedTypeSymbol currentType,
            INamedTypeSymbol componentType,
            IFieldSymbol templateField,
            string templateName,
            bool isInputField,
            bool isRetainField,
            string? fieldPath,
            LocationInfo componentLocation,
            LocationInfo declLocation,
            Action<DiagnosticInfo> reportDiagnostic,
            HashSet<INamedTypeSymbol> visited
        )
        {
            if (!visited.Add(currentType))
                return true;

            bool isValid = true;

            foreach (var member in currentType.GetMembers())
            {
                if (
                    member is not IFieldSymbol componentField
                    || componentField.IsStatic
                    || componentField.IsConst
                )
                    continue;

                string path =
                    fieldPath == null ? componentField.Name : $"{fieldPath}.{componentField.Name}";

                if (componentField.Type.IsReferenceType)
                {
                    reportDiagnostic(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.ComponentHasManagedFields,
                            componentLocation,
                            PerformanceCache.GetDisplayString(componentType),
                            templateField.Name,
                            templateName,
                            path,
                            PerformanceCache.GetDisplayString(componentField.Type)
                        )
                    );
                    isValid = false;
                    // Don't descend into reference types — they're already rejected and
                    // recursing through a class could cycle.
                    continue;
                }

                // TRECS137: anchors never belong in a component, input or persistent.
                if (
                    !ValidateNoAnchorField(
                        componentField,
                        componentType,
                        templateField,
                        path,
                        declLocation,
                        reportDiagnostic
                    )
                )
                {
                    isValid = false;
                }

                if (isInputField)
                {
                    if (
                        !ValidateInputComponentFieldType(
                            componentField,
                            componentType,
                            templateField,
                            templateName,
                            isRetainField,
                            path,
                            declLocation,
                            reportDiagnostic
                        )
                    )
                    {
                        isValid = false;
                    }
                }
                else
                {
                    // TRECS136: the converse of TRECS121 — input pointers must not
                    // escape into persistent components.
                    if (
                        !ValidatePersistentComponentFieldType(
                            componentField,
                            componentType,
                            templateField,
                            path,
                            declLocation,
                            reportDiagnostic
                        )
                    )
                    {
                        isValid = false;
                    }
                }

                // Descend into nested plain value-type structs. The special Trecs
                // handle/list/anchor types are checked by the rules above and are not
                // worth walking into (their internals are framework-owned); only
                // recurse into user/plain structs that aren't in the Trecs namespace
                // and aren't a known special type.
                if (
                    componentField.Type is INamedTypeSymbol nestedType
                    && ShouldRecurseInto(nestedType)
                )
                {
                    if (
                        !WalkComponentFields(
                            nestedType,
                            componentType,
                            templateField,
                            templateName,
                            isInputField,
                            isRetainField,
                            path,
                            componentLocation,
                            declLocation,
                            reportDiagnostic,
                            visited
                        )
                    )
                    {
                        isValid = false;
                    }
                }
            }

            return isValid;
        }

        // Whether the recursive field walk should descend into <paramref name="type"/>.
        // We only recurse into plain user-defined structs — never into primitives/enums
        // (no Trecs-forbidden fields possible), never into the Trecs framework's own
        // pointer/list/anchor/handle types (the rules already classify those at this
        // level, and their internals are framework-owned), and never into types outside
        // the user's own assemblies (e.g. BCL/Unity structs, whose layout we don't police).
        private static bool ShouldRecurseInto(INamedTypeSymbol type)
        {
            if (type.TypeKind != TypeKind.Struct)
                return false;
            // Primitives / enums have no nested component-relevant fields.
            if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum)
                return false;
            // Trecs framework types (pointers, lists, anchors, handles) — classified by
            // the rules above; don't walk their internals.
            if (SymbolAnalyzer.IsInNamespace(type.ContainingNamespace, TrecsNamespaces.Trecs))
                return false;
            // Only descend into types defined in source we're compiling. Skip metadata
            // types (BCL, Unity) — their internal layout isn't ours to police, and
            // walking them is both noisy and potentially expensive.
            if (type.Locations.All(loc => !loc.IsInSource))
                return false;
            return true;
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
        // the time Retain would re-apply the component — and never safe in a
        // *persistent* component (TRECS136), where the retained handle outlives the
        // frame-scoped lifetime guarantee entirely.
        private static readonly string[] InputPtrTypeNames =
        {
            "InputNativeUniquePtr",
            "InputNativeSharedPtr",
            "InputUniquePtr",
            "InputSharedPtr",
        };

        // Ambient cache-pin handle types: never valid inside any component (TRECS137) —
        // their PtrHandle is a live BlobCache handle that does not survive serialization.
        private static readonly string[] AnchorTypeNames = { "NativeSharedAnchor", "SharedAnchor" };

        private static bool ValidateInputComponentFieldType(
            IFieldSymbol componentField,
            INamedTypeSymbol componentType,
            IFieldSymbol templateField,
            string templateName,
            bool isRetain,
            string fieldPath,
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
                            fieldPath,
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
                        fieldPath,
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
                        fieldPath,
                        PerformanceCache.GetDisplayString(named)
                    )
                );
                isValid = false;
            }

            return isValid;
        }

        // TRECS136: input pointers must not escape into persistent (non-[Input]) components —
        // they are frame-scoped handles whose retention beyond the delivering frame is
        // history-locker-dependent (non-deterministic), and snapshotting one stores a bare id
        // replay cannot honor. The converse of TRECS121.
        private static bool ValidatePersistentComponentFieldType(
            IFieldSymbol componentField,
            INamedTypeSymbol componentType,
            IFieldSymbol templateField,
            string fieldPath,
            LocationInfo fallbackLocation,
            Action<DiagnosticInfo> reportDiagnostic
        )
        {
            if (componentField.Type is not INamedTypeSymbol named)
                return true;
            if (!SymbolAnalyzer.IsInNamespace(named.ContainingNamespace, TrecsNamespaces.Trecs))
                return true;
            if (!InputPtrTypeNames.Contains(named.Name) || named.TypeArguments.Length != 1)
                return true;

            var fieldLoc = componentField.Locations.FirstOrDefault();
            var location = fieldLoc != null ? LocationInfo.From(fieldLoc) : fallbackLocation;
            reportDiagnostic(
                DiagnosticInfo.Create(
                    DiagnosticDescriptors.PersistentComponentHasInputPtrField,
                    location,
                    fieldPath,
                    PerformanceCache.GetDisplayString(componentType),
                    templateField.Name,
                    PerformanceCache.GetDisplayString(named)
                )
            );
            return false;
        }

        // TRECS137: anchors (ambient BlobCache pins) are never valid component state — their
        // PtrHandle is a live cache handle that does not survive serialization, in any component
        // flavor (snapshotted persistent state or the recording's input stream).
        private static bool ValidateNoAnchorField(
            IFieldSymbol componentField,
            INamedTypeSymbol componentType,
            IFieldSymbol templateField,
            string fieldPath,
            LocationInfo fallbackLocation,
            Action<DiagnosticInfo> reportDiagnostic
        )
        {
            if (componentField.Type is not INamedTypeSymbol named)
                return true;
            if (!SymbolAnalyzer.IsInNamespace(named.ContainingNamespace, TrecsNamespaces.Trecs))
                return true;

            foreach (var anchorName in AnchorTypeNames)
            {
                if (named.Name == anchorName && named.TypeArguments.Length == 1)
                {
                    var fieldLoc = componentField.Locations.FirstOrDefault();
                    var location =
                        fieldLoc != null ? LocationInfo.From(fieldLoc) : fallbackLocation;
                    reportDiagnostic(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.ComponentHasAnchorField,
                            location,
                            fieldPath,
                            PerformanceCache.GetDisplayString(componentType),
                            templateField.Name,
                            PerformanceCache.GetDisplayString(named)
                        )
                    );
                    return false;
                }
            }
            return true;
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
