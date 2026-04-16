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
            SourceProductionContext context,
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

            foreach (var attr in PerformanceCache.GetAttributes(methodSymbol))
            {
                if (attr.AttributeClass?.Name != attributeName)
                    continue;
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

            if (singleTag != null && tagTypes.Count > 0)
            {
                context.ReportDiagnostic(
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

            return new IterationCriteria(tagTypes, setTypes, matchByComponents);
        }
    }
}
