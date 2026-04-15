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
        public bool IsFixedUpdateOnly { get; }
        public bool IsVariableUpdateOnly { get; }
        public bool IsConstant { get; }
        public bool IsInput { get; }
        public string InputFrameBehaviour { get; }
        public bool InputWarnOnMissing { get; }
        public bool HasExplicitDefault { get; }

        public TemplateComponentData(
            string fieldName,
            string componentTypeFullName,
            bool isInterpolated,
            bool isFixedUpdateOnly,
            bool isVariableUpdateOnly,
            bool isConstant,
            bool isInput,
            string inputFrameBehaviour,
            bool inputWarnOnMissing,
            bool hasExplicitDefault
        )
        {
            FieldName = fieldName;
            ComponentTypeFullName = componentTypeFullName;
            IsInterpolated = isInterpolated;
            IsFixedUpdateOnly = isFixedUpdateOnly;
            IsVariableUpdateOnly = isVariableUpdateOnly;
            IsConstant = isConstant;
            IsInput = isInput;
            InputFrameBehaviour = inputFrameBehaviour;
            InputWarnOnMissing = inputWarnOnMissing;
            HasExplicitDefault = hasExplicitDefault;
        }
    }

    /// <summary>
    /// Parsed data for a single IState interface on a template struct
    /// </summary>
    internal class TemplateStateData
    {
        public ImmutableArray<string> TagTypeNames { get; }

        public TemplateStateData(ImmutableArray<string> tagTypeNames)
        {
            TagTypeNames = tagTypeNames;
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
        public ImmutableArray<string> ContainingTypes { get; }
        public ImmutableArray<string> TagTypeNames { get; }
        public ImmutableArray<string> BaseTemplateTypeNames { get; }
        public ImmutableArray<TemplateComponentData> Components { get; }
        public ImmutableArray<TemplateStateData> States { get; }

        public TemplateDefinitionData(
            string typeName,
            string namespaceName,
            string accessibility,
            bool isClass,
            bool isGlobals,
            ImmutableArray<string> containingTypes,
            ImmutableArray<string> tagTypeNames,
            ImmutableArray<string> baseTemplateTypeNames,
            ImmutableArray<TemplateComponentData> components,
            ImmutableArray<TemplateStateData> states
        )
        {
            TypeName = typeName;
            NamespaceName = namespaceName;
            Accessibility = accessibility;
            IsClass = isClass;
            IsGlobals = isGlobals;
            ContainingTypes = containingTypes;
            TagTypeNames = tagTypeNames;
            BaseTemplateTypeNames = baseTemplateTypeNames;
            Components = components;
            States = states;
        }
    }
}
