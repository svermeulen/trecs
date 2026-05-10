using System.Collections.Immutable;

namespace Trecs.SourceGen.Template
{
    /// <summary>
    /// Parsed data for a single component field in a template struct
    /// </summary>
    internal class TemplateComponentData
    {
        public string FieldName { get; }
        public string ComponentTypeFullName { get; }
        public bool IsInterpolated { get; }
        public bool IsVariableUpdateOnly { get; }
        public bool IsConstant { get; }
        public bool IsInput { get; }
        public string OnMissing { get; }
        public bool HasExplicitDefault { get; }

        public TemplateComponentData(
            string fieldName,
            string componentTypeFullName,
            bool isInterpolated,
            bool isVariableUpdateOnly,
            bool isConstant,
            bool isInput,
            string inputFrameBehaviour,
            bool hasExplicitDefault
        )
        {
            FieldName = fieldName;
            ComponentTypeFullName = componentTypeFullName;
            IsInterpolated = isInterpolated;
            IsVariableUpdateOnly = isVariableUpdateOnly;
            IsConstant = isConstant;
            IsInput = isInput;
            OnMissing = inputFrameBehaviour;
            HasExplicitDefault = hasExplicitDefault;
        }
    }

    /// <summary>
    /// Parsed data for a single IPartitionedBy interface on a template — one partition
    /// dimension whose variant tags are mutually exclusive.
    /// </summary>
    internal class TemplateDimensionData
    {
        public ImmutableArray<string> VariantTagTypeNames { get; }

        public TemplateDimensionData(ImmutableArray<string> variantTagTypeNames)
        {
            VariantTagTypeNames = variantTagTypeNames;
        }
    }

    /// <summary>
    /// Complete parsed data for a template struct or class declaration
    /// </summary>
    internal class TemplateDefinitionData
    {
        public string TypeName { get; }
        public string NamespaceName { get; }
        public string Accessibility { get; }
        public bool IsClass { get; }
        public bool IsGlobals { get; }
        public bool IsVariableUpdateOnly { get; }
        public ImmutableArray<string> ContainingTypes { get; }
        public ImmutableArray<string> TagTypeNames { get; }
        public ImmutableArray<string> BaseTemplateTypeNames { get; }
        public ImmutableArray<TemplateComponentData> Components { get; }
        public ImmutableArray<TemplateDimensionData> Dimensions { get; }

        public TemplateDefinitionData(
            string typeName,
            string namespaceName,
            string accessibility,
            bool isClass,
            bool isGlobals,
            bool isVariableUpdateOnly,
            ImmutableArray<string> containingTypes,
            ImmutableArray<string> tagTypeNames,
            ImmutableArray<string> baseTemplateTypeNames,
            ImmutableArray<TemplateComponentData> components,
            ImmutableArray<TemplateDimensionData> dimensions
        )
        {
            TypeName = typeName;
            NamespaceName = namespaceName;
            Accessibility = accessibility;
            IsClass = isClass;
            IsGlobals = isGlobals;
            IsVariableUpdateOnly = isVariableUpdateOnly;
            ContainingTypes = containingTypes;
            TagTypeNames = tagTypeNames;
            BaseTemplateTypeNames = baseTemplateTypeNames;
            Components = components;
            Dimensions = dimensions;
        }
    }
}
