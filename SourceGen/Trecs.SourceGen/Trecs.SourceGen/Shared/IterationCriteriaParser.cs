#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    internal class IterationCriteria
    {
        public List<ITypeSymbol> TagTypes { get; }
        public List<ITypeSymbol> SetTypes { get; }
        public bool MatchByComponents { get; }

        public IterationCriteria(
            List<ITypeSymbol> tagTypes,
            List<ITypeSymbol> setTypes,
            bool matchByComponents
        )
        {
            TagTypes = tagTypes;
            SetTypes = setTypes;
            MatchByComponents = matchByComponents;
        }
    }

    internal static class IterationCriteriaParser
    {
        internal static string ExtractAttributeName(string name)
        {
            var simple = name.Split('.').Last();
            // Strip generic arity ([ForEachEntity<Tag>] → "ForEachEntity") so the
            // returned name matches Roslyn's INamedTypeSymbol.Name (which also has
            // the arity stripped).
            int genericStart = simple.IndexOf('<');
            if (genericStart >= 0)
                simple = simple.Substring(0, genericStart);
            return simple.EndsWith("Attribute") ? simple : simple + "Attribute";
        }

        /// <summary>
        /// Parses an iteration attribute (<c>[ForEachEntity]</c> or <c>[SingleEntity]</c>)
        /// for Tags / Tag / Set / MatchByComponents named arguments.
        /// </summary>
        /// <param name="attributeName">
        /// The attribute class name to match (e.g. <c>TrecsAttributeNames.EntityFilter</c>
        /// or <c>TrecsAttributeNames.SingleEntity</c>).
        /// </param>
        internal static IterationCriteria? ParseIterationAttribute(
            System.Action<Diagnostic> reportDiagnostic,
            MethodDeclarationSyntax method,
            IMethodSymbol methodSymbol,
            string containerName,
            string attributeName = "ForEachEntityAttribute"
        )
        {
            var tagTypes = new List<ITypeSymbol>();
            var setTypes = new List<ITypeSymbol>();
            ITypeSymbol? singleTag = null;
            bool matchByComponents = false;
            // Positional ctor: [ForEachEntity(typeof(A))] / [ForEachEntity(typeof(A), typeof(B))].
            var ctorTags = new List<ITypeSymbol>();
            // C# 11 generic-attribute form: [ForEachEntity<A>] / [ForEachEntity<A, B>].
            // Roslyn's Name strips arity, so the same attributeName matches both.
            var genericTags = new List<ITypeSymbol>();

            foreach (var attr in PerformanceCache.GetAttributes(methodSymbol))
            {
                if (attr.AttributeClass?.Name != attributeName)
                    continue;
                if (attr.AttributeClass is INamedTypeSymbol namedAttrClass)
                {
                    foreach (var typeArg in namedAttrClass.TypeArguments)
                    {
                        if (typeArg.TypeKind != TypeKind.TypeParameter)
                            genericTags.Add(typeArg);
                    }
                }
                foreach (var ctorArg in attr.ConstructorArguments)
                {
                    if (ctorArg.Kind == TypedConstantKind.Array)
                    {
                        foreach (var element in ctorArg.Values)
                            if (
                                element.Kind == TypedConstantKind.Type
                                && element.Value is ITypeSymbol cet
                            )
                                ctorTags.Add(cet);
                    }
                    else if (
                        ctorArg.Kind == TypedConstantKind.Type
                        && ctorArg.Value is ITypeSymbol ct
                    )
                    {
                        ctorTags.Add(ct);
                    }
                }
                foreach (var named in attr.NamedArguments)
                {
                    switch (named.Key)
                    {
                        case "Tags" when named.Value.Kind == TypedConstantKind.Array:
                            foreach (var element in named.Value.Values)
                                if (
                                    element.Kind == TypedConstantKind.Type
                                    && element.Value is ITypeSymbol t
                                )
                                    tagTypes.Add(t);
                            break;
                        case "Tag"
                            when named.Value.Kind == TypedConstantKind.Type
                                && named.Value.Value is ITypeSymbol t1:
                            singleTag = t1;
                            break;
                        case "Set"
                            when named.Value.Kind == TypedConstantKind.Type
                                && named.Value.Value is ITypeSymbol s1:
                            setTypes.Add(s1);
                            break;
                        case "MatchByComponents" when named.Value.Value is bool b:
                            matchByComponents = b;
                            break;
                    }
                }
                break;
            }

            // Strip "Attribute" suffix for diagnostic messages.
            var shortName = attributeName.EndsWith("Attribute")
                ? attributeName.Substring(0, attributeName.Length - "Attribute".Length)
                : attributeName;

            // Mixing tag-source forms is ambiguous (named setters would silently
            // overwrite ctor-set Tags; generic args + Tag/Tags double-specifies).
            bool hasNamedTags = singleTag != null || tagTypes.Count > 0;
            bool hasCtorTags = ctorTags.Count > 0;
            bool hasGenericTags = genericTags.Count > 0;
            int sourcesPresent =
                (hasNamedTags ? 1 : 0) + (hasCtorTags ? 1 : 0) + (hasGenericTags ? 1 : 0);
            if (sourcesPresent > 1)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TagAndTagsBothSpecified,
                        method.Identifier.GetLocation(),
                        method.Identifier.Text,
                        shortName
                    )
                );
                return null;
            }
            if (singleTag != null && tagTypes.Count > 0)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TagAndTagsBothSpecified,
                        method.Identifier.GetLocation(),
                        method.Identifier.Text,
                        shortName
                    )
                );
                return null;
            }
            if (singleTag != null)
                tagTypes.Add(singleTag);
            if (hasCtorTags)
                tagTypes.AddRange(ctorTags);
            if (hasGenericTags)
                tagTypes.AddRange(genericTags);

            return new IterationCriteria(tagTypes, setTypes, matchByComponents);
        }
    }
}
