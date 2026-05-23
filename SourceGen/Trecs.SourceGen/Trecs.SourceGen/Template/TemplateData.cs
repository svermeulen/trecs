using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Template
{
    /// <summary>
    /// Parsed data for a single component field in a template struct. Equatable record so it
    /// can flow through the incremental-generator pipeline without breaking the cache.
    /// </summary>
    internal sealed record TemplateComponentData(
        string FieldName,
        string ComponentTypeFullName,
        bool IsInterpolated,
        bool IsVariableUpdateOnly,
        bool IsConstant,
        bool IsInput,
        string OnMissing,
        bool HasExplicitDefault
    );

    /// <summary>
    /// Parsed data for a single <c>IPartitionedBy</c> interface on a template — one partition
    /// dimension whose variant tags are mutually exclusive. Arity 1 = presence/absence
    /// (the dim has the tag or doesn't); arity ≥ 2 = explicit-variants (one tag is active
    /// per dim).
    /// </summary>
    internal sealed record TemplateDimensionData(EquatableArray<string> VariantTagTypeNames)
    {
        /// <summary>
        /// True for arity-1 dimensions (<c>IPartitionedBy&lt;T&gt;</c>), where the tag is
        /// either present or absent on the entity. False for explicit-variants dimensions
        /// (<c>IPartitionedBy&lt;T1, T2, ...&gt;</c>).
        /// </summary>
        public bool IsPresenceAbsence => VariantTagTypeNames.Length == 1;
    }

    /// <summary>
    /// Complete parsed data for a template struct or class declaration. Holds only strings,
    /// bools, and value-equatable arrays — no symbols or syntax — so the cache survives.
    /// </summary>
    internal sealed record TemplateDefinitionData(
        string TypeName,
        string NamespaceName,
        string Accessibility,
        bool IsClass,
        bool IsAbstract,
        bool IsGlobals,
        bool IsVariableUpdateOnly,
        EquatableArray<string> ContainingTypes,
        EquatableArray<string> TagTypeNames,
        EquatableArray<string> BaseTemplateTypeNames,
        EquatableArray<TemplateComponentData> Components,
        EquatableArray<TemplateDimensionData> Dimensions
    );

    /// <summary>
    /// Pipeline-boundary model produced by <see cref="TemplateModelBuilder"/>. Carries the
    /// parsed definition plus any validation diagnostics; the terminal stage emits source
    /// only when <see cref="IsValid"/>, but always replays diagnostics.
    /// </summary>
    internal sealed record TemplateModel(
        TemplateDefinitionData Data,
        string HintFileName,
        bool IsValid,
        EquatableArray<DiagnosticInfo> Diagnostics
    );
}
