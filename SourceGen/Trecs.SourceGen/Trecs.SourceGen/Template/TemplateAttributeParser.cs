using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Template
{
    /// <summary>
    /// Parses template class declarations: extracts tags, base templates, partitions, components, and defaults
    /// </summary>
    internal static class TemplateAttributeParser
    {
        /// <summary>
        /// Parses a template class symbol into a TemplateDefinitionData
        /// </summary>
        public static TemplateDefinitionData Parse(
            INamedTypeSymbol symbol,
            TypeDeclarationSyntax syntax,
            Action<Diagnostic>? reportDiagnostic = null,
            Location? location = null
        )
        {
            var typeName = symbol.Name;
            var namespaceName = SymbolAnalyzer.GetNamespaceChain(symbol);
            var accessibility = SymbolAnalyzer.GetAccessibilityModifier(symbol);
            var containingTypes = SymbolAnalyzer.GetContainingTypeChain(symbol).ToImmutableArray();
            var isClass = symbol.TypeKind == TypeKind.Class;

            var tagTypeNames = ExtractTagTypeNames(symbol);
            var baseTemplateTypeNames = ExtractBaseTemplateTypeNames(symbol);
            var isGlobals = IsGlobalsTemplate(symbol);
            var isVariableUpdateOnly = HasAttribute(
                symbol.GetAttributes(),
                "VariableUpdateOnlyAttribute"
            );
            var partitions = ExtractPartitions(symbol);
            var defaultInitializedFields = GetDefaultInitializedFields(syntax);
            var components = ExtractComponents(symbol, defaultInitializedFields);

            return new TemplateDefinitionData(
                typeName,
                namespaceName,
                accessibility,
                isClass,
                isGlobals,
                isVariableUpdateOnly,
                containingTypes,
                tagTypeNames,
                baseTemplateTypeNames,
                components,
                partitions
            );
        }

        /// <summary>
        /// Extracts tag type names from ITagged&lt;T1, T2, ...&gt; interfaces
        /// </summary>
        private static ImmutableArray<string> ExtractTagTypeNames(INamedTypeSymbol symbol)
        {
            var tags = new List<string>();

            foreach (var iface in symbol.Interfaces)
            {
                if (IsITaggedInterface(iface))
                {
                    foreach (var typeArg in iface.TypeArguments)
                    {
                        tags.Add(PerformanceCache.GetDisplayString(typeArg));
                    }
                }
            }

            return tags.ToImmutableArray();
        }

        /// <summary>
        /// Extracts base template type names from IExtends&lt;T1, T2, ...&gt; interfaces
        /// </summary>
        private static ImmutableArray<string> ExtractBaseTemplateTypeNames(INamedTypeSymbol symbol)
        {
            var baseTemplates = new List<string>();

            foreach (var iface in symbol.Interfaces)
            {
                if (IsIExtendsInterface(iface))
                {
                    foreach (var typeArg in iface.TypeArguments)
                    {
                        baseTemplates.Add(PerformanceCache.GetDisplayString(typeArg));
                    }
                }
            }

            return baseTemplates.ToImmutableArray();
        }

        /// <summary>
        /// Extracts partition combinations from IHasPartition&lt;T1, T2, ...&gt; interfaces
        /// </summary>
        private static ImmutableArray<TemplatePartitionData> ExtractPartitions(
            INamedTypeSymbol symbol
        )
        {
            var partitions = new List<TemplatePartitionData>();

            foreach (var iface in symbol.Interfaces)
            {
                if (IsIHasPartitionInterface(iface))
                {
                    var tagNames = iface
                        .TypeArguments.Select(t => PerformanceCache.GetDisplayString(t))
                        .ToImmutableArray();
                    partitions.Add(new TemplatePartitionData(tagNames));
                }
            }

            return partitions.ToImmutableArray();
        }

        /// <summary>
        /// Extracts component field data from the struct's fields
        /// </summary>
        private static ImmutableArray<TemplateComponentData> ExtractComponents(
            INamedTypeSymbol symbol,
            ImmutableHashSet<string>? defaultInitializedFields
        )
        {
            var components = new List<TemplateComponentData>();

            foreach (var member in symbol.GetMembers())
            {
                if (member is IFieldSymbol field && !field.IsStatic && !field.IsConst)
                {
                    var attrs = field.GetAttributes();

                    bool isInterpolated = HasAttribute(attrs, "InterpolatedAttribute");
                    bool isVariableUpdateOnly = HasAttribute(attrs, "VariableUpdateOnlyAttribute");
                    bool isConstant = HasAttribute(attrs, "ConstantAttribute");
                    bool hasExplicitDefault =
                        defaultInitializedFields?.Contains(field.Name) ?? false;

                    bool isInput = false;
                    string? inputFrameBehaviour = null;
                    bool inputWarnOnMissing = false;

                    var inputAttr = attrs.FirstOrDefault(a =>
                        a.AttributeClass?.Name == "InputAttribute"
                    );
                    if (inputAttr != null)
                    {
                        isInput = true;
                        if (inputAttr.ConstructorArguments.Length >= 1)
                        {
                            // The enum value is stored as an int; reconstruct the fully qualified enum member
                            var enumArg = inputAttr.ConstructorArguments[0];
                            if (enumArg.Type != null && enumArg.Value != null)
                            {
                                // Get the enum member name from the value
                                var enumType = enumArg.Type;
                                var enumMembers = enumType
                                    .GetMembers()
                                    .OfType<IFieldSymbol>()
                                    .Where(f => f.HasConstantValue);
                                foreach (var enumMember in enumMembers)
                                {
                                    if (Equals(enumMember.ConstantValue, enumArg.Value))
                                    {
                                        inputFrameBehaviour =
                                            $"{PerformanceCache.GetDisplayString(enumType)}.{enumMember.Name}";
                                        break;
                                    }
                                }
                            }
                        }
                        if (
                            inputAttr.ConstructorArguments.Length >= 2
                            && inputAttr.ConstructorArguments[1].Value is bool warn
                        )
                        {
                            inputWarnOnMissing = warn;
                        }
                    }

                    components.Add(
                        new TemplateComponentData(
                            fieldName: field.Name,
                            componentTypeFullName: PerformanceCache.GetDisplayString(field.Type),
                            isInterpolated: isInterpolated,
                            isVariableUpdateOnly: isVariableUpdateOnly,
                            isConstant: isConstant,
                            isInput: isInput,
                            inputFrameBehaviour: inputFrameBehaviour ?? string.Empty,
                            inputWarnOnMissing: inputWarnOnMissing,
                            hasExplicitDefault: hasExplicitDefault
                        )
                    );
                }
            }

            return components.ToImmutableArray();
        }

        /// <summary>
        /// Scans non-static fields for EqualsValueClause initializers.
        /// Returns null if no fields have initializers.
        /// </summary>
        private static ImmutableHashSet<string>? GetDefaultInitializedFields(
            TypeDeclarationSyntax syntax
        )
        {
            var builder = ImmutableHashSet.CreateBuilder<string>();

            foreach (var member in syntax.Members)
            {
                if (member is FieldDeclarationSyntax fieldDecl)
                {
                    if (fieldDecl.Modifiers.Any(m => m.Text == "static"))
                    {
                        continue;
                    }

                    foreach (var variable in fieldDecl.Declaration.Variables)
                    {
                        if (variable.Initializer is EqualsValueClauseSyntax)
                        {
                            builder.Add(variable.Identifier.Text);
                        }
                    }
                }
            }

            return builder.Count > 0 ? builder.ToImmutable() : null;
        }

        private static bool IsGlobalsTemplate(INamedTypeSymbol symbol)
        {
            foreach (var iface in symbol.Interfaces)
            {
                if (!iface.IsGenericType || !IsInTrecsNamespace(iface))
                    continue;

                // Check ITagged<TrecsTags.Globals> (the base globals template itself)
                if (
                    iface.OriginalDefinition.Name == "ITagged"
                    && iface.TypeArguments.Length >= 1
                    && iface.TypeArguments[0].Name == "Globals"
                    && iface.TypeArguments[0].ContainingType?.Name == "TrecsTags"
                )
                {
                    return true;
                }

                // Check IExtends<TrecsTemplates.Globals> (user-defined globals templates)
                if (
                    iface.OriginalDefinition.Name == "IExtends"
                    && iface.TypeArguments.Length >= 1
                    && iface.TypeArguments[0].Name == "Globals"
                    && iface.TypeArguments[0].ContainingType?.Name == "TrecsTemplates"
                )
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsITaggedInterface(INamedTypeSymbol iface)
        {
            return iface.IsGenericType
                && iface.OriginalDefinition.Name == "ITagged"
                && IsInTrecsNamespace(iface);
        }

        private static bool IsIExtendsInterface(INamedTypeSymbol iface)
        {
            return iface.IsGenericType
                && iface.OriginalDefinition.Name == "IExtends"
                && IsInTrecsNamespace(iface);
        }

        private static bool IsIHasPartitionInterface(INamedTypeSymbol iface)
        {
            return iface.IsGenericType
                && iface.OriginalDefinition.Name == "IHasPartition"
                && IsInTrecsNamespace(iface);
        }

        private static bool IsInTrecsNamespace(INamedTypeSymbol symbol)
        {
            var ns = symbol.ContainingNamespace;
            return ns != null
                && !ns.IsGlobalNamespace
                && PerformanceCache.GetDisplayString(ns) == "Trecs";
        }

        private static bool HasAttribute(ImmutableArray<AttributeData> attrs, string attributeName)
        {
            return attrs.Any(a => a.AttributeClass?.Name == attributeName);
        }
    }
}
