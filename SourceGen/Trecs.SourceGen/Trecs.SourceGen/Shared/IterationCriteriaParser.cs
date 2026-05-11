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
        public List<ITypeSymbol> WithoutTagTypes { get; }
        public bool MatchByComponents { get; }

        public IterationCriteria(
            List<ITypeSymbol> tagTypes,
            List<ITypeSymbol> setTypes,
            bool matchByComponents,
            List<ITypeSymbol>? withoutTagTypes = null
        )
        {
            TagTypes = tagTypes;
            SetTypes = setTypes;
            MatchByComponents = matchByComponents;
            WithoutTagTypes = withoutTagTypes ?? new List<ITypeSymbol>();
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
        /// for Tags / Tag / Set / MatchByComponents named arguments. Tag-source
        /// extraction (positional / generic / named) and the TRECS053
        /// mutual-exclusion check are delegated to <see cref="TagSourceParser"/>;
        /// only the iteration-specific <c>Set</c> / <c>MatchByComponents</c>
        /// extras are read here.
        /// </summary>
        /// <param name="attributeName">
        /// The attribute class name to match (e.g. <c>TrecsAttributeNames.ForEachEntity</c>
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
            var setTypes = new List<ITypeSymbol>();
            var withoutTagTypes = new List<ITypeSymbol>();
            bool matchByComponents = false;
            AttributeData? matched = null;

            foreach (var attr in PerformanceCache.GetAttributes(methodSymbol))
            {
                if (attr.AttributeClass?.Name != attributeName)
                    continue;
                matched = attr;
                foreach (var named in attr.NamedArguments)
                {
                    switch (named.Key)
                    {
                        case "Set"
                            when named.Value.Kind == TypedConstantKind.Type
                                && named.Value.Value is ITypeSymbol s1:
                            setTypes.Add(s1);
                            break;
                        case "MatchByComponents" when named.Value.Value is bool b:
                            matchByComponents = b;
                            break;
                        case "Without"
                            when named.Value.Kind == TypedConstantKind.Type
                                && named.Value.Value is ITypeSymbol w1:
                            withoutTagTypes.Add(w1);
                            break;
                        case "Withouts" when named.Value.Kind == TypedConstantKind.Array:
                            foreach (var elem in named.Value.Values)
                            {
                                if (
                                    elem.Kind == TypedConstantKind.Type
                                    && elem.Value is ITypeSymbol ws
                                )
                                    withoutTagTypes.Add(ws);
                            }
                            break;
                    }
                }
                break;
            }

            // Strip "Attribute" suffix for diagnostic messages.
            var shortName = TagSourceParser.StripAttributeSuffix(attributeName);

            if (matched == null)
                return new IterationCriteria(
                    new List<ITypeSymbol>(),
                    setTypes,
                    matchByComponents,
                    withoutTagTypes
                );

            var result = TagSourceParser.Parse(
                matched,
                reportDiagnostic,
                method.Identifier.GetLocation(),
                method.Identifier.Text,
                shortName
            );
            if (!result.Ok)
                return null;

            return new IterationCriteria(
                result.TagTypes!,
                setTypes,
                matchByComponents,
                withoutTagTypes
            );
        }
    }
}
