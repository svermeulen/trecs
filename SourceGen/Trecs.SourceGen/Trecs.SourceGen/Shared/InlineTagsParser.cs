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
    /// <para>
    /// Tag-source extraction (positional / generic / named) and the TRECS053
    /// mutual-exclusion check are delegated to <see cref="TagSourceParser"/>;
    /// only the FromWorld-specific tag-count cap is enforced here.
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
            var result = TagSourceParser.Parse(
                attribute,
                reportDiagnostic,
                diagnosticLocation,
                targetName,
                attributeShortName
            );
            if (!result.Ok)
                return null;

            var tagTypes = result.TagTypes!;
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
            string fullName = attributeShortName.EndsWith("Attribute")
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
                    TagSourceParser.StripAttributeSuffix(attributeShortName),
                    reportDiagnostic
                );
            }
            return new List<ITypeSymbol>();
        }
    }
}
