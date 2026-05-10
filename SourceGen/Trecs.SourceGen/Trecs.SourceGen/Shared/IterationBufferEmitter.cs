#nullable enable

using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Emits the three iteration-buffer scheduling chains shared by both job generators
    /// (Job and AutoJob) across both their dense and sparse schedule paths. Each method
    /// expects the iteration buffers in the form `(ComponentType, ReadOnly)` returned by
    /// each generator's GetIterationBuffers helper.
    ///
    /// Implicit caller-scope contract: `{prefix}deps`, `{prefix}scheduler`, `{prefix}world`,
    /// `{prefix}group`, and `{prefix}handle` must be in scope for the methods that use them
    /// (registration uses deps + scheduler + group; materialization uses world + group;
    /// output tracking uses scheduler + handle + group).
    /// </summary>
    internal static class IterationBufferEmitter
    {
        /// <summary>
        /// Emits the per-buffer dependency chain that folds component-buffer reads/writes
        /// into the per-group dependency handle.
        /// </summary>
        internal static void EmitDepRegistration(
            StringBuilder sb,
            string indent,
            IReadOnlyList<(ITypeSymbol Type, bool ReadOnly)> buffers
        )
        {
            var p = FromWorldEmitter.GenPrefix;
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var rid =
                    $"ResourceId.Component(ComponentTypeId<{PerformanceCache.GetDisplayString(type)}>.Value)";
                var method = readOnly ? "IncludeReadDep" : "IncludeWriteDep";
                sb.AppendLine(
                    $"{indent}{p}deps = {p}scheduler.{method}({p}deps, {rid}, {p}group);"
                );
            }
        }

        /// <summary>
        /// Emits the materialization of each iteration buffer into a per-group local
        /// (`_trecs_buf{i}_value`) via the world's per-job buffer accessors.
        /// </summary>
        internal static void EmitMaterialization(
            StringBuilder sb,
            string indent,
            IReadOnlyList<(ITypeSymbol Type, bool ReadOnly)> buffers
        )
        {
            var p = FromWorldEmitter.GenPrefix;
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var typeName = PerformanceCache.GetDisplayString(type);
                var ext = readOnly ? "GetBufferReadForJob" : "GetBufferWriteForJob";
                sb.AppendLine(
                    $"{indent}var ({p}buf{i}_value, _) = {p}world.{ext}<{typeName}>({p}group);"
                );
            }
        }

        /// <summary>
        /// Emits the per-buffer output-tracking calls so the scheduler observes which
        /// component buffers the scheduled job reads or writes.
        /// </summary>
        internal static void EmitOutputTracking(
            StringBuilder sb,
            string indent,
            IReadOnlyList<(ITypeSymbol Type, bool ReadOnly)> buffers
        )
        {
            var p = FromWorldEmitter.GenPrefix;
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var rid =
                    $"ResourceId.Component(ComponentTypeId<{PerformanceCache.GetDisplayString(type)}>.Value)";
                var method = readOnly ? "TrackJobRead" : "TrackJobWrite";
                sb.AppendLine($"{indent}{p}scheduler.{method}({p}handle, {rid}, {p}group);");
            }
        }
    }
}
