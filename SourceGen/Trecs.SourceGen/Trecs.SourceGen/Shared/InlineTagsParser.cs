#nullable enable

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Shared parser for inline <c>Tag</c> / <c>Tags</c> named arguments on
    /// <c>[FromWorld]</c> / <c>[SingleEntity]</c> attributes. Returns the resolved
    /// tag-type list, or <c>null</c> when validation fails (with a diagnostic already
    /// reported).
    /// <para>
    /// Validates: <c>Tag</c> and <c>Tags</c> are mutually exclusive; the resolved list
    /// has at most 4 entries (matching the highest <c>WithTags&lt;T1, T2, T3, T4&gt;</c>
    /// runtime overload). Empty / missing inline tags are returned as an empty list —
    /// the caller decides whether that's acceptable for the kind being parsed.
    /// </para>
    /// </summary>
    internal static class InlineTagsParser
    {
        /// <summary>
        /// Parse inline <c>Tag</c> / <c>Tags</c> off a single attribute's named arguments.
        /// </summary>
        /// <param name="attribute">The <c>[FromWorld]</c> or <c>[SingleEntity]</c> attribute data.</param>
        /// <param name="diagnosticLocation">Where to anchor any reported diagnostic.</param>
        /// <param name="targetName">Display name used in diagnostic messages (parameter / field name).</param>
        /// <param name="attributeShortName">Attribute short name (<c>"FromWorld"</c> / <c>"SingleEntity"</c>) used in diagnostics.</param>
        /// <param name="reportDiagnostic">Diagnostic sink.</param>
        /// <returns>The resolved tag list, or <c>null</c> on validation failure.</returns>
        public static List<ITypeSymbol>? Parse(
            AttributeData attribute,
            Location diagnosticLocation,
            string targetName,
            string attributeShortName,
            System.Action<Diagnostic> reportDiagnostic
        )
        {
            var tagTypes = new List<ITypeSymbol>();
            ITypeSymbol? singleTag = null;
            // Positional ctor: [SingleEntity(typeof(A))] / [SingleEntity(typeof(A), typeof(B))]
            // expanded as either a single Type arg or an array via params Type[].
            var ctorTags = new List<ITypeSymbol>();
            // C# 11 generic-attribute form: [SingleEntity<A>] / [SingleEntity<A, B>].
            // Tags are pulled from the attribute class's TypeArguments. Roslyn returns
            // the same Name ("SingleEntityAttribute") for the generic and non-generic
            // variants, so callers' attribute-name matching already routes both here.
            var genericTags = new List<ITypeSymbol>();
            if (attribute.AttributeClass is INamedTypeSymbol namedAttrClass)
            {
                foreach (var typeArg in namedAttrClass.TypeArguments)
                {
                    // TypeParameterSymbol means an unbound generic — skip; only add
                    // concrete ITypeSymbol args.
                    if (typeArg.TypeKind != TypeKind.TypeParameter)
                        genericTags.Add(typeArg);
                }
            }
            foreach (var ctorArg in attribute.ConstructorArguments)
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
            foreach (var named in attribute.NamedArguments)
            {
                switch (named.Key)
                {
                    case "Tags" when named.Value.Kind == TypedConstantKind.Array:
                        foreach (var element in named.Value.Values)
                            if (
                                element.Kind == TypedConstantKind.Type
                                && element.Value is ITypeSymbol et
                            )
                                tagTypes.Add(et);
                        break;
                    case "Tag"
                        when named.Value.Kind == TypedConstantKind.Type
                            && named.Value.Value is ITypeSymbol t1:
                        singleTag = t1;
                        break;
                }
            }
            // Mixing tag-source forms is ambiguous (named setters would silently
            // overwrite ctor-set Tags; generic args + Tag/Tags double-specifies).
            // Reuse the existing "both Tag and Tags" diagnostic — same root cause.
            bool hasNamedTags = singleTag != null || tagTypes.Count > 0;
            bool hasCtorTags = ctorTags.Count > 0;
            bool hasGenericTags = genericTags.Count > 0;
            int sourcesPresent = (hasNamedTags ? 1 : 0) + (hasCtorTags ? 1 : 0) + (hasGenericTags ? 1 : 0);
            if (sourcesPresent > 1)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TagAndTagsBothSpecified,
                        diagnosticLocation,
                        targetName,
                        attributeShortName
                    )
                );
                return null;
            }
            if (singleTag != null && tagTypes.Count > 0)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.TagAndTagsBothSpecified,
                        diagnosticLocation,
                        targetName,
                        attributeShortName
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
            if (tagTypes.Count > 4)
            {
                // TagSet<T1, T2, T3, T4>.Value is the highest runtime overload.
                // Reuse the same diagnostic FromWorld already emits — same root cause,
                // same advice.
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.FromWorldTooManyInlineTags,
                        diagnosticLocation,
                        targetName,
                        tagTypes.Count
                    )
                );
                return null;
            }
            return tagTypes;
        }

        /// <summary>
        /// Walk a parameter symbol's attributes for the named one and parse its inline
        /// tags. Returns the resolved list (empty if attribute isn't found or has no
        /// inline tags), or <c>null</c> on validation failure.
        /// </summary>
        public static List<ITypeSymbol>? ParseFromSymbol(
            ISymbol symbol,
            string attributeShortName,
            Location diagnosticLocation,
            string targetName,
            System.Action<Diagnostic> reportDiagnostic
        )
        {
            // attributeShortName is the simple attribute name w/ "Attribute" suffix.
            // Symbols always carry the full FQN — match against the Name.
            string fullName =
                attributeShortName.EndsWith("Attribute")
                    ? attributeShortName
                    : attributeShortName + "Attribute";
            foreach (var attr in PerformanceCache.GetAttributes(symbol))
            {
                if (attr.AttributeClass?.Name != fullName)
                    continue;
                return Parse(
                    attr,
                    diagnosticLocation,
                    targetName,
                    attributeShortName.EndsWith("Attribute")
                        ? attributeShortName.Substring(
                            0,
                            attributeShortName.Length - "Attribute".Length
                        )
                        : attributeShortName,
                    reportDiagnostic
                );
            }
            return new List<ITypeSymbol>();
        }
    }
}
