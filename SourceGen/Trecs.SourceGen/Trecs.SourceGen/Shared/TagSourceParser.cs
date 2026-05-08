#nullable enable

using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Which tag-source form was present on a parsed attribute.
    /// </summary>
    internal enum TagSourceForm
    {
        /// <summary>No tags specified in any form.</summary>
        None,

        /// <summary>Positional ctor: <c>[Foo(typeof(A))]</c> / <c>[Foo(typeof(A), typeof(B))]</c>.</summary>
        Positional,

        /// <summary>C# 11 generic-attribute form: <c>[Foo&lt;A&gt;]</c> / <c>[Foo&lt;A, B&gt;]</c>.</summary>
        Generic,

        /// <summary>Named arguments: <c>[Foo(Tag = typeof(A))]</c> / <c>[Foo(Tags = new[]{...})]</c>.</summary>
        Named,
    }

    /// <summary>
    /// Result of parsing tag-source forms off a single attribute.
    /// </summary>
    internal readonly struct TagSourceParseResult
    {
        public readonly List<ITypeSymbol>? TagTypes;
        public readonly TagSourceForm Form;

        public TagSourceParseResult(List<ITypeSymbol>? tagTypes, TagSourceForm form)
        {
            TagTypes = tagTypes;
            Form = form;
        }

        /// <summary>
        /// True if the parse succeeded (no diagnostic emitted).
        /// </summary>
        public bool Ok => TagTypes != null;

        public static TagSourceParseResult Failed() => new(null, TagSourceForm.None);
    }

    /// <summary>
    /// Shared parser for the three tag-source forms an attribute may carry:
    /// positional ctor (<c>[Foo(typeof(A))]</c>), C# 11 generic args
    /// (<c>[Foo&lt;A&gt;]</c>), and named arguments (<c>[Foo(Tag = ...)]</c> /
    /// <c>[Foo(Tags = new[]{...})]</c>). Validates mutual exclusion across the
    /// three forms (and <c>Tag</c> vs <c>Tags</c> within Named) by emitting
    /// TRECS053 (<see cref="DiagnosticDescriptors.TagAndTagsBothSpecified"/>).
    /// <para>
    /// Each call site (iteration attributes, FromWorld/SingleEntity inline tags)
    /// wraps this parser to layer on its own attribute-specific extras (<c>Set</c>
    /// / <c>MatchByComponents</c> for iteration; tag-count cap for FromWorld).
    /// </para>
    /// </summary>
    internal static class TagSourceParser
    {
        /// <summary>
        /// Parse positional / generic / named tag sources off a single attribute,
        /// validate they aren't mixed, and return the resolved tag list plus the
        /// form that was used (for downstream context).
        /// </summary>
        /// <param name="attribute">Attribute data to inspect.</param>
        /// <param name="reportDiagnostic">Diagnostic sink.</param>
        /// <param name="diagnosticLocation">Where to anchor any reported diagnostic.</param>
        /// <param name="targetName">Display name used in diagnostic messages (method / parameter / field).</param>
        /// <param name="attributeShortName">Attribute short name without the <c>Attribute</c> suffix (e.g. <c>"ForEachEntity"</c>) for diagnostic messages.</param>
        /// <returns>
        /// A successful result with the resolved tag list (may be empty) and form,
        /// or a failed result with <see cref="TagSourceParseResult.TagTypes"/>
        /// null when validation failed (a diagnostic has already been reported).
        /// </returns>
        public static TagSourceParseResult Parse(
            AttributeData attribute,
            System.Action<Diagnostic> reportDiagnostic,
            Location diagnosticLocation,
            string targetName,
            string attributeShortName
        )
        {
            var tagTypes = new List<ITypeSymbol>();
            ITypeSymbol? singleTag = null;
            // Positional ctor: [Foo(typeof(A))] / [Foo(typeof(A), typeof(B))]
            // expanded as either a single Type arg or an array via params Type[].
            var ctorTags = new List<ITypeSymbol>();
            // C# 11 generic-attribute form: [Foo<A>] / [Foo<A, B>].
            // Tags are pulled from the attribute class's TypeArguments. Roslyn
            // returns the same Name (with arity stripped) for the generic and
            // non-generic variants, so callers' attribute-name matching already
            // routes both through here.
            var genericTags = new List<ITypeSymbol>();

            if (attribute.AttributeClass is INamedTypeSymbol namedAttrClass)
            {
                foreach (var typeArg in namedAttrClass.TypeArguments)
                {
                    // TypeParameterSymbol means an unbound generic — skip; only
                    // add concrete ITypeSymbol args.
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
                else if (ctorArg.Kind == TypedConstantKind.Type && ctorArg.Value is ITypeSymbol ct)
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
                        diagnosticLocation,
                        targetName,
                        attributeShortName
                    )
                );
                return TagSourceParseResult.Failed();
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
                return TagSourceParseResult.Failed();
            }

            TagSourceForm form;
            if (singleTag != null)
            {
                tagTypes.Add(singleTag);
                form = TagSourceForm.Named;
            }
            else if (hasNamedTags)
            {
                form = TagSourceForm.Named;
            }
            else if (hasCtorTags)
            {
                tagTypes.AddRange(ctorTags);
                form = TagSourceForm.Positional;
            }
            else if (hasGenericTags)
            {
                tagTypes.AddRange(genericTags);
                form = TagSourceForm.Generic;
            }
            else
            {
                form = TagSourceForm.None;
            }

            return new TagSourceParseResult(tagTypes, form);
        }

        /// <summary>
        /// Strip the "Attribute" suffix from an attribute name for use in
        /// diagnostic messages (the user wrote <c>[Foo]</c>, not <c>[FooAttribute]</c>).
        /// </summary>
        public static string StripAttributeSuffix(string attributeName)
        {
            const string suffix = "Attribute";
            return attributeName.EndsWith(suffix)
                ? attributeName.Substring(0, attributeName.Length - suffix.Length)
                : attributeName;
        }
    }
}
