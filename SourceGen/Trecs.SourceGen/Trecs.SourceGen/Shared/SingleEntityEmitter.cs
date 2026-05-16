#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Aspect;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Shared <c>[SingleEntity]</c> schedule-time emit shared by Phase 3
    /// (<c>[WrapAsJob]</c> static methods, lowered into auto-generated jobs by
    /// <c>AutoJobGenerator</c>) and Phase 4 (hand-written job structs whose
    /// fields carry <c>[SingleEntity]</c>, processed by <c>JobGenerator</c>).
    /// <para>
    /// The two phases differ only in the carrier shape and the LHS of the
    /// per-job-instance field assignment — the rest of the emit (hoisted
    /// EntityIndex resolution, dep registration, buffer fetching + aspect
    /// construction, dep tracking) is identical and lives here.
    /// </para>
    /// </summary>
    internal static class SingleEntityEmitter
    {
        /// <summary>
        /// Carrier-agnostic shape consumed by the emitter. Both
        /// <c>JobGenerator.SingleEntityFieldEntry</c> and
        /// <c>AutoJobGenerator.AutoJobParam</c> implement it.
        /// </summary>
        internal interface IEmitTarget
        {
            /// <summary>
            /// Root used for the per-target local names (e.g. "_trecs_se_<root>_ei",
            /// "_trecs_se_<root>_b{i}"). For Phase 4 this is the user's field name;
            /// for Phase 3 it's the parameter name.
            /// </summary>
            string LocalNameRoot { get; }

            /// <summary>
            /// LHS of the <c>_trecs_job.X = ...</c> assignment. For Phase 4 this is
            /// the user's field name; for Phase 3 it's the auto-generated field
            /// (<c>_trecs_se_&lt;name&gt;</c> for aspects, with a <c>_read</c> /
            /// <c>_write</c> suffix for component fields).
            /// </summary>
            string JobFieldAssignmentLhs { get; }

            bool IsAspect { get; }

            /// <summary>
            /// True iff the target is a component-typed singleton resolved as a
            /// write (<c>ref</c> param or <c>NativeComponentWrite</c> field). Ignored
            /// when <see cref="IsAspect"/> is true.
            /// </summary>
            bool IsComponentWrite { get; }

            /// <summary>
            /// Inline <c>Tag</c> / <c>Tags</c> from the <c>[SingleEntity]</c>
            /// attribute. Always non-empty (TRECS114 enforces this).
            /// </summary>
            IReadOnlyList<ITypeSymbol> TagTypes { get; }

            /// <summary>
            /// Aspect read/write component types — non-null exactly when
            /// <see cref="IsAspect"/> is true.
            /// </summary>
            AspectAttributeData? AspectData { get; }

            /// <summary>Aspect type display string. Non-null iff aspect.</summary>
            string? AspectTypeDisplay { get; }

            /// <summary>
            /// Component type display string. Non-null iff not aspect — this is the
            /// <c>T</c> in <c>NativeComponent{Read,Write}&lt;T&gt;</c>.
            /// </summary>
            string? ComponentTypeDisplay { get; }
        }

        const string GenPrefix = FromWorldEmitter.GenPrefix;

        static string EiLocal(IEmitTarget e) => $"{GenPrefix}se_{e.LocalNameRoot}_ei";

        static string WithTagsClause(IEmitTarget e) =>
            $"WithTags<{string.Join(", ", e.TagTypes.Select(PerformanceCache.GetDisplayString))}>()";

        /// <summary>
        /// Pre-loop EntityIndex resolution: one local per target, fetched via
        /// <c>Query().WithTags&lt;...&gt;().SingleIndex()</c>.
        /// </summary>
        internal static void EmitHoistedSetup(
            StringBuilder sb,
            string body,
            IEnumerable<IEmitTarget> targets
        )
        {
            foreach (var e in targets)
            {
                var ei = EiLocal(e);
                sb.AppendLine(
                    $"{body}var {ei} = {GenPrefix}world.Query().{WithTagsClause(e)}.SingleIndex();"
                );
            }
        }

        /// <summary>
        /// Per-job-instance dep registration — one IncludeRead/WriteDep call per
        /// (component, group) pair the singleton touches.
        /// </summary>
        internal static void EmitDepRegistration(
            StringBuilder sb,
            string body,
            IEnumerable<IEmitTarget> targets
        )
        {
            foreach (var e in targets)
            {
                var ei = EiLocal(e);
                if (e.IsAspect)
                {
                    var aspectData = e.AspectData!;
                    foreach (var comp in aspectData.ReadTypes)
                    {
                        var n = PerformanceCache.GetDisplayString(comp);
                        sb.AppendLine(
                            $"{body}{GenPrefix}deps = {GenPrefix}scheduler.IncludeReadDep({GenPrefix}deps, ResourceId.Component(ComponentTypeId<{n}>.Value), {ei}.GroupIndex);"
                        );
                    }
                    foreach (var comp in aspectData.WriteTypes)
                    {
                        var n = PerformanceCache.GetDisplayString(comp);
                        sb.AppendLine(
                            $"{body}{GenPrefix}deps = {GenPrefix}scheduler.IncludeWriteDep({GenPrefix}deps, ResourceId.Component(ComponentTypeId<{n}>.Value), {ei}.GroupIndex);"
                        );
                    }
                }
                else
                {
                    var method = e.IsComponentWrite ? "IncludeWriteDep" : "IncludeReadDep";
                    sb.AppendLine(
                        $"{body}{GenPrefix}deps = {GenPrefix}scheduler.{method}({GenPrefix}deps, ResourceId.Component(ComponentTypeId<{e.ComponentTypeDisplay}>.Value), {ei}.GroupIndex);"
                    );
                }
            }
        }

        /// <summary>
        /// Per-job-instance field assignments. Aspects: fetch per-component
        /// buffers in canonical order and construct the aspect via its
        /// <c>EntityIndex</c> ctor; components: assign the resolved
        /// <c>NativeComponent{Read,Write}</c>.
        /// </summary>
        internal static void EmitFieldAssignment(
            StringBuilder sb,
            string body,
            IEnumerable<IEmitTarget> targets
        )
        {
            foreach (var e in targets)
            {
                var ei = EiLocal(e);
                if (e.IsAspect)
                {
                    // Use AspectAttributeData.AllComponentTypes — canonical order matches
                    // the aspect's generated EntityIndex ctor so positional buffer args
                    // line up by construction.
                    var aspectData = e.AspectData!;
                    var allComponents = aspectData.AllComponentTypes;
                    var bufferLocals = new List<string>(allComponents.Length);
                    for (int i = 0; i < allComponents.Length; i++)
                    {
                        var compType = allComponents[i];
                        bool inWrite = aspectData.WriteTypes.Any(w =>
                            SymbolEqualityComparer.Default.Equals(w, compType)
                        );
                        var ext = inWrite ? "GetBufferWriteForJob" : "GetBufferReadForJob";
                        var local = $"{GenPrefix}se_{e.LocalNameRoot}_b{i}";
                        bufferLocals.Add(local);
                        sb.AppendLine(
                            $"{body}var ({local}, _) = {GenPrefix}world.{ext}<{PerformanceCache.GetDisplayString(compType)}>({ei}.GroupIndex);"
                        );
                    }
                    sb.AppendLine(
                        $"{body}{GenPrefix}job.{e.JobFieldAssignmentLhs} = new {e.AspectTypeDisplay}({ei}, {string.Join(", ", bufferLocals)});"
                    );
                }
                else
                {
                    var method = e.IsComponentWrite
                        ? "GetNativeComponentWriteForJob"
                        : "GetNativeComponentReadForJob";
                    sb.AppendLine(
                        $"{body}{GenPrefix}job.{e.JobFieldAssignmentLhs} = {GenPrefix}world.{method}<{e.ComponentTypeDisplay}>({ei});"
                    );
                }
            }
        }

        /// <summary>
        /// Per-job-instance dep tracking — same shape as dep registration but
        /// fires <c>TrackJobRead</c>/<c>TrackJobWrite</c> against the issued
        /// <c>_trecs_handle</c>.
        /// </summary>
        internal static void EmitTracking(
            StringBuilder sb,
            string body,
            IEnumerable<IEmitTarget> targets
        )
        {
            foreach (var e in targets)
            {
                var ei = EiLocal(e);
                if (e.IsAspect)
                {
                    var aspectData = e.AspectData!;
                    foreach (var comp in aspectData.ReadTypes)
                    {
                        var n = PerformanceCache.GetDisplayString(comp);
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.TrackJobRead({GenPrefix}handle, ResourceId.Component(ComponentTypeId<{n}>.Value), {ei}.GroupIndex);"
                        );
                    }
                    foreach (var comp in aspectData.WriteTypes)
                    {
                        var n = PerformanceCache.GetDisplayString(comp);
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.TrackJobWrite({GenPrefix}handle, ResourceId.Component(ComponentTypeId<{n}>.Value), {ei}.GroupIndex);"
                        );
                    }
                }
                else
                {
                    var method = e.IsComponentWrite ? "TrackJobWrite" : "TrackJobRead";
                    sb.AppendLine(
                        $"{body}{GenPrefix}scheduler.{method}({GenPrefix}handle, ResourceId.Component(ComponentTypeId<{e.ComponentTypeDisplay}>.Value), {ei}.GroupIndex);"
                    );
                }
            }
        }
    }
}
