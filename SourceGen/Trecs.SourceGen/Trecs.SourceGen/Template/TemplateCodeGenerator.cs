using System.Collections.Generic;
using System.Collections.Immutable;
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
                    // C# requires every partial declaration to agree on `abstract` — mirror it here.
                    var abstractModifier = data.IsAbstract ? "abstract " : "";

                    builder.AppendLine(
                        indentLevel,
                        $"{effectiveAccessibility} {abstractModifier}partial {typeKeyword} {data.TypeName}"
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
            // For templates with explicit defaults, generate a static defaults instance.
            // Abstract templates can't be `new`'d directly — emit a private concrete
            // subclass so the field initializers still run and feed the Template ctor.
            if (data.Components.Any(c => c.HasExplicitDefault))
            {
                if (data.IsAbstract)
                {
                    sb.AppendLine(
                        indentLevel,
                        $"private sealed class _DefaultsHolder : {data.TypeName} {{ }}"
                    );
                    sb.AppendLine(
                        indentLevel,
                        $"private static readonly {data.TypeName} _templateDefaults = new _DefaultsHolder();"
                    );
                }
                else
                {
                    sb.AppendLine(
                        indentLevel,
                        $"private static readonly {data.TypeName} _templateDefaults = new();"
                    );
                }
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

            // partitions — cross product of declared dimensions. Presence/absence dims
            // (arity 1) expand to two cases: the tag present, and an "absent" entry
            // (no tag added). The cross product walker filters out the empty all-absent
            // partition when no dim is active.
            var partitionTagSets = ComputeCrossProduct(data.Dimensions);
            if (partitionTagSets.Count > 0)
            {
                sb.AppendLine(argIndent, "partitions: new TagSet[]");
                sb.AppendLine(argIndent, "{");
                for (int i = 0; i < partitionTagSets.Count; i++)
                {
                    var tags = partitionTagSets[i];
                    string entry;
                    if (tags.Count == 0)
                    {
                        // All dims are presence/absence and all "absent" in this row —
                        // the partition's identifying tag set is empty.
                        entry = "TagSet.Null";
                    }
                    else
                    {
                        var tagArgs = string.Join(", ", tags);
                        entry = $"TagSet<{tagArgs}>.Value";
                    }
                    var comma = i < partitionTagSets.Count - 1 ? "," : "";
                    sb.AppendLine(argIndent + 1, $"{entry}{comma}");
                }
                sb.AppendLine(argIndent, "},");
            }
            else
            {
                sb.AppendLine(argIndent, "partitions: Array.Empty<TagSet>(),");
            }

            // dimensions — one TagSet per dimension listing its variant tags
            if (data.Dimensions.Length > 0)
            {
                sb.AppendLine(argIndent, "dimensions: new TagSet[]");
                sb.AppendLine(argIndent, "{");
                for (int i = 0; i < data.Dimensions.Length; i++)
                {
                    var variants = data.Dimensions[i].VariantTagTypeNames;
                    var tagArgs = string.Join(", ", variants);
                    var comma = i < data.Dimensions.Length - 1 ? "," : "";
                    sb.AppendLine(argIndent + 1, $"TagSet<{tagArgs}>.Value{comma}");
                }
                sb.AppendLine(argIndent, "},");
            }
            else
            {
                sb.AppendLine(argIndent, "dimensions: Array.Empty<TagSet>(),");
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
                $"localVariableUpdateOnly: {(data.IsVariableUpdateOnly ? "true" : "false")},"
            );

            // isAbstract — set when the source template class is declared `abstract`.
            // Such templates may be IExtends<> bases but cannot be passed to
            // WorldBuilder.AddTemplate (analyzer TRECS039 + runtime guard).
            sb.AppendLine(argIndent, $"isAbstract: {(data.IsAbstract ? "true" : "false")}");

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
                    $"new ComponentDeclaration<{component.ComponentTypeFullName}>(null, null, null, null, null, null),"
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
            }
            else
            {
                sb.AppendLine(paramIndent, "inputFrameBehaviour: null,");
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

        /// <summary>
        /// Computes the cross product across a template's partition dimensions. Each result
        /// is one concrete partition: a list of variant tag type names (one per active
        /// dimension; presence/absence dimensions in the "absent" state contribute no
        /// entry). Empty input yields an empty result.
        /// </summary>
        private static IReadOnlyList<IReadOnlyList<string>> ComputeCrossProduct(
            ImmutableArray<TemplateDimensionData> dimensions
        )
        {
            if (dimensions.Length == 0)
            {
                return System.Array.Empty<IReadOnlyList<string>>();
            }

            var result = new List<IReadOnlyList<string>>();
            var current = new List<string>();
            BuildCrossProduct(dimensions, 0, current, result);
            return result;
        }

        // Sentinel marking the "absent" branch of a presence/absence dimension. The walker
        // skips it instead of recording a tag, so the resulting partition has one fewer
        // entry than there are active dimensions.
        const string AbsentVariant = "<absent>";

        private static void BuildCrossProduct(
            ImmutableArray<TemplateDimensionData> dimensions,
            int dimIndex,
            List<string> current,
            List<IReadOnlyList<string>> result
        )
        {
            if (dimIndex == dimensions.Length)
            {
                result.Add(current.ToArray());
                return;
            }

            var dim = dimensions[dimIndex];
            if (dim.IsPresenceAbsence)
            {
                // "Absent" branch — skip adding a tag.
                BuildCrossProduct(dimensions, dimIndex + 1, current, result);
                // "Present" branch — add the tag.
                current.Add(dim.VariantTagTypeNames[0]);
                BuildCrossProduct(dimensions, dimIndex + 1, current, result);
                current.RemoveAt(current.Count - 1);
            }
            else
            {
                foreach (var variant in dim.VariantTagTypeNames)
                {
                    current.Add(variant);
                    BuildCrossProduct(dimensions, dimIndex + 1, current, result);
                    current.RemoveAt(current.Count - 1);
                }
            }
        }
    }
}
