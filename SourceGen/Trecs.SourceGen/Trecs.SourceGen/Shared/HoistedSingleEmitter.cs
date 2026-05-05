#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Shared emitter for <c>[SingleEntity]</c> hoist preambles. Used by
    /// <c>RunOnceGenerator</c> (the entire body) and the two <c>[ForEachEntity]</c>
    /// generators (before the iteration loop) so the hoist code shape stays in sync
    /// across all paths.
    /// <para>
    /// Per call this emits, for each <see cref="HoistedSingletonInfo"/>:
    /// <list type="number">
    ///   <item><c>var __&lt;Name&gt;_ei = &lt;world&gt;.Query().WithTags&lt;Tags...&gt;().SingleEntityIndex();</c></item>
    ///   <item>For aspects: per-component buffer locals (<c>__&lt;Name&gt;_b{i}</c>) and the
    ///     aspect view (<c>var __&lt;Name&gt; = new MyAspect(__&lt;Name&gt;_ei, ...);</c>).</item>
    ///   <item>For components: a <c>ref readonly</c> / <c>ref</c> alias <c>__&lt;Name&gt;</c>
    ///     into the component buffer at the matched index.</item>
    /// </list>
    /// The user method then receives <c>in __&lt;Name&gt;</c> (or <c>ref __&lt;Name&gt;</c>) for
    /// each hoisted slot, in declaration order, via the call-args builder in each
    /// generator.
    /// </para>
    /// </summary>
    internal static class HoistedSingleEmitter
    {
        /// <summary>
        /// Emits the hoist preamble for every <see cref="HoistedSingletonInfo"/> in
        /// <paramref name="hoisted"/>. No-op when the list is empty.
        /// </summary>
        /// <param name="sb">Target builder.</param>
        /// <param name="indentLevel">Indent level for the emitted lines.</param>
        /// <param name="worldVar">Name of the in-scope <c>WorldAccessor</c> local — typically <c>"__world"</c>.</param>
        /// <param name="hoisted">Hoisted-singleton infos in user declaration order.</param>
        public static void Emit(
            OptimizedStringBuilder sb,
            int indentLevel,
            string worldVar,
            List<HoistedSingletonInfo> hoisted
        )
        {
            foreach (var info in hoisted)
            {
                if (info.IsAspect)
                    EmitAspect(sb, indentLevel, worldVar, info);
                else
                    EmitComponent(sb, indentLevel, worldVar, info);
            }
        }

        private static void EmitAspect(
            OptimizedStringBuilder sb,
            int indentLevel,
            string worldVar,
            HoistedSingletonInfo info
        )
        {
            sb.AppendLine(
                indentLevel,
                $"// [SingleEntity] {info.ParamName} : {info.AspectTypeDisplay}"
            );
            var withTagsArgs = string.Join(
                ", ",
                info.TagTypes.Select(t => PerformanceCache.GetDisplayString(t))
            );
            var eiVar = $"__{info.ParamName}_ei";
            sb.AppendLine(
                indentLevel,
                $"var {eiVar} = {worldVar}.Query().WithTags<{withTagsArgs}>().SingleEntityIndex();"
            );

            // The aspect's generated EntityIndex constructor takes buffers in
            // AspectData.AllComponentTypes order — same canonical helper used by
            // AspectCodeGenerator. Reusing it here keeps the orderings locked.
            var aspectData = info.AspectData!;
            var allComponents = aspectData.AllComponentTypes;
            var bufferVars = new List<string>(allComponents.Length);
            for (int i = 0; i < allComponents.Length; i++)
            {
                var componentType = allComponents[i];
                bool inWrite = aspectData.WriteTypes.Any(t =>
                    SymbolEqualityComparer.Default.Equals(t, componentType)
                );
                var suffix = inWrite ? "Write" : "Read";
                var bufferVar = $"__{info.ParamName}_b{i}";
                bufferVars.Add(bufferVar);
                sb.AppendLine(
                    indentLevel,
                    $"var {bufferVar} = {worldVar}.ComponentBuffer<{PerformanceCache.GetDisplayString(componentType)}>({eiVar}.GroupIndex).{suffix};"
                );
            }
            sb.AppendLine(
                indentLevel,
                $"var __{info.ParamName} = new {info.AspectTypeDisplay}({eiVar}, {string.Join(", ", bufferVars)});"
            );
        }

        private static void EmitComponent(
            OptimizedStringBuilder sb,
            int indentLevel,
            string worldVar,
            HoistedSingletonInfo info
        )
        {
            sb.AppendLine(
                indentLevel,
                $"// [SingleEntity] {info.ParamName} : {info.ComponentTypeDisplay}"
            );
            var withTagsArgs = string.Join(
                ", ",
                info.TagTypes.Select(t => PerformanceCache.GetDisplayString(t))
            );
            var eiVar = $"__{info.ParamName}_ei";
            sb.AppendLine(
                indentLevel,
                $"var {eiVar} = {worldVar}.Query().WithTags<{withTagsArgs}>().SingleEntityIndex();"
            );
            var aliasModifier = info.IsRef ? "ref" : "ref readonly";
            var bufferSuffix = info.IsRef ? "Write" : "Read";
            sb.AppendLine(
                indentLevel,
                $"{aliasModifier} var __{info.ParamName} = ref {worldVar}.ComponentBuffer<{info.ComponentTypeDisplay}>({eiVar}.GroupIndex).{bufferSuffix}[{eiVar}.Index];"
            );
        }

        /// <summary>
        /// Adds containing namespaces for every type referenced by the hoisted
        /// singletons (tag types, aspect type, aspect's read/write component types,
        /// component type) into <paramref name="namespaces"/>. Skips System and the
        /// global namespace.
        /// </summary>
        public static void CollectNamespaces(
            HashSet<string> namespaces,
            List<HoistedSingletonInfo> hoisted,
            string globalNamespaceName
        )
        {
            void Add(ITypeSymbol? sym)
            {
                if (sym == null)
                    return;
                var ns = PerformanceCache.GetDisplayString(sym.ContainingNamespace);
                if (
                    !string.IsNullOrEmpty(ns)
                    && ns != "System"
                    && !ns.StartsWith("System.")
                    && ns != globalNamespaceName
                )
                    namespaces.Add(ns);
            }

            foreach (var info in hoisted)
            {
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
            }
        }
    }
}
