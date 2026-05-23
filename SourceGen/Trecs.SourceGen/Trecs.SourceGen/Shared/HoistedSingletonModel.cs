#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Aspect;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Value-equality projection of <see cref="HoistedSingletonInfo"/> for use in
    /// Roslyn incremental-generator pipelines. The legacy info type holds
    /// <see cref="ITypeSymbol"/> references — which compare by reference identity and
    /// therefore break the cache across compilation pumps. This model carries only
    /// pre-computed strings + <see cref="EquatableArray{T}"/>s so two transforms that
    /// produce the same logical hoist code compare equal regardless of which
    /// <c>Compilation</c> they came from.
    /// <para>
    /// Build via <see cref="HoistedSingletonModelBuilder.FromInfo"/> at the transform
    /// boundary (where symbols are still alive); thereafter store the model in the
    /// pipeline and emit from it via <see cref="HoistedSingleEmitter.Emit"/>.
    /// </para>
    /// </summary>
    internal readonly record struct HoistedSingletonModel(
        string ParamName,
        bool IsAspect,
        bool IsRef,
        EquatableArray<string> TagDisplays,
        string AspectTypeDisplay,
        EquatableArray<HoistedAspectComponent> AspectComponents,
        string ComponentTypeDisplay,
        EquatableArray<string> Namespaces
    );

    /// <summary>
    /// One slot in <see cref="HoistedSingletonModel.AspectComponents"/>, in the canonical
    /// order produced by <see cref="AspectAttributeData.AllComponentTypes"/>. The
    /// <see cref="IsWrite"/> flag picks the buffer's <c>.Write</c> vs <c>.Read</c>
    /// projection when emitting the per-component buffer locals.
    /// </summary>
    internal readonly record struct HoistedAspectComponent(string TypeDisplay, bool IsWrite);

    /// <summary>
    /// Projects a symbol-bearing <see cref="HoistedSingletonInfo"/> into the equatable
    /// <see cref="HoistedSingletonModel"/>. Run at the transform-phase boundary, after
    /// <see cref="ParameterClassifier.ClassifyHoistedSingleton"/> has validated the
    /// inline tags / modifiers, but before the value is published to the pipeline.
    /// </summary>
    internal static class HoistedSingletonModelBuilder
    {
        public static HoistedSingletonModel FromInfo(HoistedSingletonInfo info)
        {
            var tagDisplays = info
                .TagTypes.Select(PerformanceCache.GetDisplayString)
                .ToEquatableArray();

            EquatableArray<HoistedAspectComponent> aspectComponents;
            if (info.IsAspect && info.AspectData != null)
            {
                var data = info.AspectData;
                var components = new HoistedAspectComponent[data.AllComponentTypes.Length];
                for (int i = 0; i < data.AllComponentTypes.Length; i++)
                {
                    var componentType = data.AllComponentTypes[i];
                    bool isWrite = data.WriteTypes.Any(t =>
                        SymbolEqualityComparer.Default.Equals(t, componentType)
                    );
                    components[i] = new HoistedAspectComponent(
                        PerformanceCache.GetDisplayString(componentType),
                        isWrite
                    );
                }
                aspectComponents = new EquatableArray<HoistedAspectComponent>(components);
            }
            else
            {
                aspectComponents = EquatableArray<HoistedAspectComponent>.Empty;
            }

            var namespaces = CollectNamespaces(info);

            return new HoistedSingletonModel(
                ParamName: info.ParamName,
                IsAspect: info.IsAspect,
                IsRef: info.IsRef,
                TagDisplays: tagDisplays,
                AspectTypeDisplay: info.AspectTypeDisplay ?? string.Empty,
                AspectComponents: aspectComponents,
                ComponentTypeDisplay: info.ComponentTypeDisplay ?? string.Empty,
                Namespaces: namespaces
            );
        }

        /// <summary>
        /// Pre-collects every containing-namespace string this hoisted singleton refers
        /// to: tag types (plus their containing types when nested), the aspect type, the
        /// aspect's read/write component types, or the standalone component type.
        /// <c>System</c> / <c>System.*</c> entries are filtered out — they shouldn't end
        /// up as <c>using</c> directives — but the compilation's global-namespace string
        /// is NOT filtered here because we don't know it at build time. The consumer
        /// (<see cref="HoistedSingleEmitter.CollectNamespaces"/>)
        /// drops the global ns when merging into the using set.
        /// </summary>
        private static EquatableArray<string> CollectNamespaces(HoistedSingletonInfo info)
        {
            var collected = new HashSet<string>();

            void Add(ITypeSymbol? sym)
            {
                if (sym == null)
                    return;
                var ns = PerformanceCache.GetDisplayString(sym.ContainingNamespace);
                if (!string.IsNullOrEmpty(ns) && ns != "System" && !ns.StartsWith("System."))
                    collected.Add(ns);
            }

            foreach (var t in info.TagTypes)
            {
                Add(t);
                if (t.ContainingType != null)
                    Add(t.ContainingType);
            }
            if (info.AspectData != null)
            {
                foreach (var t in info.AspectData.ReadTypes)
                    Add(t);
                foreach (var t in info.AspectData.WriteTypes)
                    Add(t);
            }
            Add(info.AspectTypeSymbol);
            Add(info.ComponentTypeSymbol);

            return collected.ToEquatableArray();
        }
    }
}
