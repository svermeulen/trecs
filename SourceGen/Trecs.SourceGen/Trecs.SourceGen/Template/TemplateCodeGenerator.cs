using System.Linq;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Template
{
    /// <summary>
    /// Emits direct Template constructor calls for a parsed template struct declaration
    /// </summary>
    internal static class TemplateCodeGenerator
    {
        /// <summary>
        /// Generates the partial struct source with the static Template field
        /// </summary>
        public static string Generate(TemplateDefinitionData data)
        {
            var sb = new OptimizedStringBuilder();

            sb.AppendUsings(CommonUsings.WithExtras("System"));

            sb.WrapInNamespace(
                data.NamespaceName,
                builder =>
                {
                    int indentLevel = 0;

                    // Open containing types
                    foreach (var containingType in data.ContainingTypes)
                    {
                        builder.AppendLine(indentLevel, $"partial class {containingType}");
                        builder.AppendLine(indentLevel, "{");
                        indentLevel++;
                    }

                    var effectiveAccessibility =
                        data.Accessibility == "private" ? "internal" : data.Accessibility;
                    var typeKeyword = data.IsClass ? "class" : "struct";

                    builder.AppendLine(
                        indentLevel,
                        $"{effectiveAccessibility} partial {typeKeyword} {data.TypeName}"
                    );
                    builder.AppendLine(indentLevel, "{");
                    indentLevel++;

                    GenerateTemplateField(builder, indentLevel, data);

                    indentLevel--;
                    builder.AppendLine(indentLevel, "}");

                    // Close containing types
                    for (int i = data.ContainingTypes.Length - 1; i >= 0; i--)
                    {
                        indentLevel--;
                        builder.AppendLine(indentLevel, "}");
                    }
                }
            );

            return sb.ToString();
        }

        private static void GenerateTemplateField(
            OptimizedStringBuilder sb,
            int indentLevel,
            TemplateDefinitionData data
        )
        {
            // For templates with explicit defaults, generate a static defaults instance
            if (data.Components.Any(c => c.HasExplicitDefault))
            {
                sb.AppendLine(
                    indentLevel,
                    $"private static readonly {data.TypeName} _templateDefaults = new();"
                );
                sb.AppendLine();
            }

            sb.AppendLine(indentLevel, "public static readonly Template Template = new Template(");

            int argIndent = indentLevel + 1;

            // debugName
            sb.AppendLine(argIndent, $"debugName: \"{data.TypeName}\",");

            // localBaseTemplates
            if (data.BaseTemplateTypeNames.Length > 0)
            {
                var baseArgs = string.Join(
                    ", ",
                    data.BaseTemplateTypeNames.Select(t => $"{t}.Template")
                );
                sb.AppendLine(argIndent, $"localBaseTemplates: new Template[] {{ {baseArgs} }},");
            }
            else
            {
                sb.AppendLine(argIndent, "localBaseTemplates: Array.Empty<Template>(),");
            }

            // partitions
            if (data.Partitions.Length > 0)
            {
                sb.AppendLine(argIndent, "partitions: new TagSet[]");
                sb.AppendLine(argIndent, "{");
                for (int i = 0; i < data.Partitions.Length; i++)
                {
                    var partition = data.Partitions[i];
                    var tagArgs = string.Join(", ", partition.TagTypeNames);
                    var comma = i < data.Partitions.Length - 1 ? "," : "";
                    sb.AppendLine(argIndent + 1, $"TagSet<{tagArgs}>.Value{comma}");
                }
                sb.AppendLine(argIndent, "},");
            }
            else
            {
                sb.AppendLine(argIndent, "partitions: Array.Empty<TagSet>(),");
            }

            // localComponentDeclarations
            if (data.Components.Length > 0)
            {
                sb.AppendLine(argIndent, "localComponentDeclarations: new IComponentDeclaration[]");
                sb.AppendLine(argIndent, "{");
                foreach (var component in data.Components)
                {
                    GenerateComponentDeclaration(sb, argIndent + 1, component, data);
                }
                sb.AppendLine(argIndent, "},");
            }
            else
            {
                sb.AppendLine(
                    argIndent,
                    "localComponentDeclarations: Array.Empty<IComponentDeclaration>(),"
                );
            }

            // localTags
            if (data.TagTypeNames.Length > 0)
            {
                var tagArgs = string.Join(", ", data.TagTypeNames.Select(t => $"Tag<{t}>.Value"));
                sb.AppendLine(argIndent, $"localTags: new Tag[] {{ {tagArgs} }},");
            }
            else
            {
                sb.AppendLine(argIndent, "localTags: Array.Empty<Tag>(),");
            }

            // localVariableUpdateOnly — template-level flag that propagates
            // VUO semantics to every component on this exact template class.
            // Inheritance is resolved transitively in WorldInfo.ResolveTemplate.
            sb.AppendLine(
                argIndent,
                $"localVariableUpdateOnly: {(data.IsVariableUpdateOnly ? "true" : "false")}"
            );

            sb.AppendLine(indentLevel, ");");
        }

        private static void GenerateComponentDeclaration(
            OptimizedStringBuilder sb,
            int indentLevel,
            TemplateComponentData component,
            TemplateDefinitionData data
        )
        {
            bool hasConfig =
                component.IsInterpolated
                || component.IsVariableUpdateOnly
                || component.IsConstant
                || component.IsInput
                || component.HasExplicitDefault;

            if (!hasConfig)
            {
                sb.AppendLine(
                    indentLevel,
                    $"new ComponentDeclaration<{component.ComponentTypeFullName}>(null, null, null, null, null, null, null),"
                );
                return;
            }

            sb.AppendLine(
                indentLevel,
                $"new ComponentDeclaration<{component.ComponentTypeFullName}>("
            );
            int paramIndent = indentLevel + 1;

            sb.AppendLine(
                paramIndent,
                $"variableUpdateOnly: {NullableBool(component.IsVariableUpdateOnly)},"
            );
            sb.AppendLine(paramIndent, $"isInput: {NullableBool(component.IsInput)},");

            if (component.IsInput)
            {
                var frameBehaviour = component.OnMissing ?? "MissingInputBehavior.Reset";
                sb.AppendLine(paramIndent, $"inputFrameBehaviour: {frameBehaviour},");
                sb.AppendLine(
                    paramIndent,
                    $"warnOnMissingInput: {(component.InputWarnOnMissing ? "true" : "false")},"
                );
            }
            else
            {
                sb.AppendLine(paramIndent, "inputFrameBehaviour: null,");
                sb.AppendLine(paramIndent, "warnOnMissingInput: null,");
            }

            sb.AppendLine(paramIndent, $"isConstant: {NullableBool(component.IsConstant)},");
            sb.AppendLine(
                paramIndent,
                $"isInterpolated: {NullableBool(component.IsInterpolated)},"
            );

            if (component.HasExplicitDefault)
            {
                sb.AppendLine(
                    paramIndent,
                    $"defaultValue: _templateDefaults.{component.FieldName}"
                );
            }
            else
            {
                sb.AppendLine(paramIndent, "defaultValue: null");
            }

            sb.AppendLine(indentLevel, "),");
        }

        private static string NullableBool(bool value)
        {
            return value ? "true" : "null";
        }
    }
}
