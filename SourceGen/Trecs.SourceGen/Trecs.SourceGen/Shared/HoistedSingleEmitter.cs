#nullable enable

using System.Collections.Generic;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Shared emitter for <c>[SingleEntity]</c> hoist preambles. Used by
    /// <c>RunOnceGenerator</c> (the entire body) and the two <c>[ForEachEntity]</c>
    /// generators (before the iteration loop) so the hoist code shape stays in sync
    /// across all paths.
    /// <para>
    /// Per call this emits, for each <see cref="HoistedSingletonModel"/>:
    /// <list type="number">
    ///   <item><c>var __&lt;Name&gt;_ei = &lt;world&gt;.Query().WithTags&lt;Tags...&gt;().SingleIndex();</c></item>
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
        public static void Emit(
            OptimizedStringBuilder sb,
            int indentLevel,
            string worldVar,
            EquatableArray<HoistedSingletonModel> hoisted
        )
        {
            foreach (var model in hoisted)
            {
                if (model.IsAspect)
                    EmitAspectModel(sb, indentLevel, worldVar, model);
                else
                    EmitComponentModel(sb, indentLevel, worldVar, model);
            }
        }

        private static void EmitAspectModel(
            OptimizedStringBuilder sb,
            int indentLevel,
            string worldVar,
            HoistedSingletonModel model
        )
        {
            sb.AppendLine(
                indentLevel,
                $"// [SingleEntity] {model.ParamName} : {model.AspectTypeDisplay}"
            );
            var withTagsArgs = string.Join(", ", model.TagDisplays);
            var eiVar = $"__{model.ParamName}_ei";
            sb.AppendLine(
                indentLevel,
                $"var {eiVar} = {worldVar}.Query().WithTags<{withTagsArgs}>().SingleIndex();"
            );

            // Buffers emitted in AspectComponents order — same canonical order the
            // aspect's generated EntityIndex constructor expects.
            var bufferVars = new List<string>(model.AspectComponents.Length);
            for (int i = 0; i < model.AspectComponents.Length; i++)
            {
                var component = model.AspectComponents[i];
                var suffix = component.IsWrite ? "Write" : "Read";
                var bufferVar = $"__{model.ParamName}_b{i}";
                bufferVars.Add(bufferVar);
                sb.AppendLine(
                    indentLevel,
                    $"var {bufferVar} = {worldVar}.ComponentBuffer<{component.TypeDisplay}>({eiVar}.GroupIndex).{suffix};"
                );
            }
            sb.AppendLine(
                indentLevel,
                $"var __{model.ParamName} = new {model.AspectTypeDisplay}({eiVar}, {string.Join(", ", bufferVars)});"
            );
        }

        private static void EmitComponentModel(
            OptimizedStringBuilder sb,
            int indentLevel,
            string worldVar,
            HoistedSingletonModel model
        )
        {
            sb.AppendLine(
                indentLevel,
                $"// [SingleEntity] {model.ParamName} : {model.ComponentTypeDisplay}"
            );
            var withTagsArgs = string.Join(", ", model.TagDisplays);
            var eiVar = $"__{model.ParamName}_ei";
            sb.AppendLine(
                indentLevel,
                $"var {eiVar} = {worldVar}.Query().WithTags<{withTagsArgs}>().SingleIndex();"
            );
            var aliasModifier = model.IsRef ? "ref" : "ref readonly";
            var bufferSuffix = model.IsRef ? "Write" : "Read";
            sb.AppendLine(
                indentLevel,
                $"{aliasModifier} var __{model.ParamName} = ref {worldVar}.ComponentBuffer<{model.ComponentTypeDisplay}>({eiVar}.GroupIndex).{bufferSuffix}[{eiVar}.Index];"
            );
        }

        /// <summary>
        /// Adds containing namespaces for every type referenced by the hoisted
        /// singleton models into <paramref name="namespaces"/>. The model already
        /// pre-collected the per-singleton namespace set (with <c>System</c>
        /// filtering applied); here we only drop the compilation's global namespace,
        /// which the model couldn't know about at build time.
        /// </summary>
        public static void CollectNamespaces(
            HashSet<string> namespaces,
            EquatableArray<HoistedSingletonModel> hoisted,
            string globalNamespaceName
        )
        {
            foreach (var model in hoisted)
            {
                foreach (var ns in model.Namespaces)
                {
                    if (ns != globalNamespaceName)
                        namespaces.Add(ns);
                }
            }
        }
    }
}
