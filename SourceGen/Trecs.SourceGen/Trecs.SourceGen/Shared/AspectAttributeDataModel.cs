#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Aspect;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Value-equality projection of <see cref="AspectAttributeData"/> for use in
    /// Roslyn incremental-generator pipelines. The legacy <see cref="AspectAttributeData"/>
    /// type stores <see cref="ITypeSymbol"/> arrays — symbol references compare by
    /// reference identity per <c>Compilation</c>, so two pipeline transforms that
    /// produce "the same" aspect data on consecutive pumps return objects that are not
    /// equal, and every downstream node has to re-run. This model carries pre-computed
    /// display strings (the only thing codegen actually uses) plus an
    /// <see cref="EquatableArray{T}"/> of namespaces, so it round-trips through the
    /// pipeline with structural equality.
    /// <para>
    /// Build via <see cref="AspectAttributeDataModelBuilder.FromData"/> at the
    /// transform-phase boundary (where the original <see cref="ITypeSymbol"/>s are
    /// still alive); thereafter store the model on the pipeline and emit purely from
    /// strings.
    /// </para>
    /// <para>
    /// <see cref="AllComponentDisplays"/> mirrors
    /// <see cref="AspectAttributeData.AllComponentTypes"/>: read types first in
    /// declaration order, then any write types not already present. Consumers that
    /// previously emitted per-component buffer locals in <c>AllComponentTypes</c>
    /// order should drive the loop off this array instead.
    /// </para>
    /// </summary>
    internal readonly record struct AspectAttributeDataModel(
        EquatableArray<string> ReadTypeDisplays,
        EquatableArray<string> WriteTypeDisplays,
        EquatableArray<string> InterfaceTypeDisplays,
        EquatableArray<string> AllComponentDisplays,
        EquatableArray<string> Namespaces
    )
    {
        public static readonly AspectAttributeDataModel Empty = new(
            EquatableArray<string>.Empty,
            EquatableArray<string>.Empty,
            EquatableArray<string>.Empty,
            EquatableArray<string>.Empty,
            EquatableArray<string>.Empty
        );

        public bool IsEmpty =>
            ReadTypeDisplays.IsEmpty && WriteTypeDisplays.IsEmpty && InterfaceTypeDisplays.IsEmpty;

        /// <summary>
        /// True if <paramref name="componentDisplay"/> appears in
        /// <see cref="WriteTypeDisplays"/>. Used at codegen time to pick the
        /// <c>.Write</c> vs <c>.Read</c> buffer projection for each component slot.
        /// </summary>
        public bool IsWrite(string componentDisplay)
        {
            foreach (var w in WriteTypeDisplays)
            {
                if (w == componentDisplay)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Projects a symbol-bearing <see cref="AspectAttributeData"/> into the equatable
    /// <see cref="AspectAttributeDataModel"/>. Run at the transform-phase boundary —
    /// the resulting model contains only strings and is safe to publish through the
    /// pipeline.
    /// </summary>
    internal static class AspectAttributeDataModelBuilder
    {
        /// <summary>
        /// Build a model from parsed <see cref="AspectAttributeData"/>. The
        /// <paramref name="globalNamespaceName"/> argument is the
        /// <c>Compilation.GlobalNamespace</c> display string; types in the global ns
        /// would otherwise produce an empty namespace entry, so we skip those here.
        /// </summary>
        public static AspectAttributeDataModel FromData(
            AspectAttributeData data,
            string globalNamespaceName
        )
        {
            var readDisplays = data
                .ReadTypes.Select(PerformanceCache.GetDisplayString)
                .ToEquatableArray();
            var writeDisplays = data
                .WriteTypes.Select(PerformanceCache.GetDisplayString)
                .ToEquatableArray();
            var interfaceDisplays = data
                .InterfaceTypes.Select(PerformanceCache.GetDisplayString)
                .ToEquatableArray();

            // AllComponentTypes order: read types first, then writes not already in read.
            // PerformanceCache.MergeDistinctTypes implements that ordering for symbols;
            // we mirror it on the string projections to keep buffer-emit order stable.
            var seen = new HashSet<string>();
            var all = new List<string>(readDisplays.Length + writeDisplays.Length);
            foreach (var s in readDisplays)
            {
                if (seen.Add(s))
                    all.Add(s);
            }
            foreach (var s in writeDisplays)
            {
                if (seen.Add(s))
                    all.Add(s);
            }
            var allDisplays = new EquatableArray<string>(all.ToArray());

            var namespaces = CollectNamespaces(data, globalNamespaceName);

            return new AspectAttributeDataModel(
                ReadTypeDisplays: readDisplays,
                WriteTypeDisplays: writeDisplays,
                InterfaceTypeDisplays: interfaceDisplays,
                AllComponentDisplays: allDisplays,
                Namespaces: namespaces
            );
        }

        /// <summary>
        /// Collect every containing-namespace string referenced by the aspect's
        /// read/write/interface types. Filters <c>System</c> / <c>System.*</c> (these
        /// don't need <c>using</c> directives in generated code) and the compilation's
        /// global namespace.
        /// </summary>
        private static EquatableArray<string> CollectNamespaces(
            AspectAttributeData data,
            string globalNamespaceName
        )
        {
            var collected = new HashSet<string>();

            void Add(ITypeSymbol? sym)
            {
                if (sym == null)
                    return;
                var ns = PerformanceCache.GetDisplayString(sym.ContainingNamespace);
                if (
                    string.IsNullOrEmpty(ns)
                    || ns == globalNamespaceName
                    || ns == "System"
                    || ns.StartsWith("System.")
                )
                    return;
                collected.Add(ns);
            }

            foreach (var t in data.ReadTypes)
                Add(t);
            foreach (var t in data.WriteTypes)
                Add(t);
            foreach (var t in data.InterfaceTypes)
                Add(t);

            return collected.ToEquatableArray();
        }
    }
}
