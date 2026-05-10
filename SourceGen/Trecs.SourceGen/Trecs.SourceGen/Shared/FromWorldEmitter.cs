#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Aspect;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    internal static class FromWorldEmitter
    {
        internal const string GenPrefix = "_trecs_";

        /// <summary>
        /// Emits TagSet resolution and group hoisting for [FromWorld] fields before the iteration loop.
        /// </summary>
        internal static void EmitFromWorldHoistedSetup(
            StringBuilder sb,
            string body,
            List<FromWorldFieldEmit> emits
        )
        {
            // For fields with inline tags, resolve the final TagSet by combining inline
            // and optional runtime tags. For fields without inline tags, the schedule
            // param is used directly.
            foreach (var e in emits)
            {
                if (e.InlineTagSetExpression.Length > 0)
                {
                    // Emit: var _trecs_fishPositions_tags = TagSet<Fish>.Value;
                    // if (fishPositionsTags != null) _trecs_fishPositions_tags = _trecs_fishPositions_tags.CombineWith(fishPositionsTags.Value);
                    sb.AppendLine($"{body}var {e.TagSetExpression} = {e.InlineTagSetExpression};");
                    if (e.HasScheduleParam)
                    {
                        sb.AppendLine(
                            $"{body}if ({e.ScheduleParamName} != null) {e.TagSetExpression} = {e.TagSetExpression}.CombineWith({e.ScheduleParamName}.Value);"
                        );
                    }
                }
            }

            // Hoist cross-group lookups (NativeComponentLookup) and single-group resolutions
            // (ComponentBuffer / NativeEntitySetIndices) outside the iteration loop. Their
            // group resolution is per-tagset, not per-iteration-group.
            foreach (var e in emits)
            {
                if (e.NeedsHoistedGroups)
                {
                    sb.AppendLine(
                        $"{body}var {e.HoistedGroupsLocal} = {GenPrefix}world.WorldInfo.GetGroupsWithTags({e.TagSetExpression});"
                    );
                }
                else if (e.NeedsHoistedSingleGroup)
                {
                    sb.AppendLine(
                        $"{body}var {e.HoistedSingleGroupLocal} = {GenPrefix}world.WorldInfo.GetSingleGroupWithTags({e.TagSetExpression});"
                    );
                }
            }
        }

        /// <summary>
        /// Emits per-field dependency registration (IncludeReadDep/IncludeWriteDep) for [FromWorld] fields.
        /// </summary>
        internal static void EmitFromWorldDepRegistration(
            StringBuilder sb,
            string body,
            List<FromWorldFieldEmit> emits
        )
        {
            foreach (var e in emits)
            {
                var t = e.GenericArgDisplay;
                switch (e.Kind)
                {
                    case FromWorldFieldKind.NativeComponentBufferRead:
                    case FromWorldFieldKind.NativeComponentBufferWrite:
                    {
                        var rid = $"ResourceId.Component(ComponentTypeId<{t}>.Value)";
                        var method =
                            e.Kind == FromWorldFieldKind.NativeComponentBufferRead
                                ? "IncludeReadDep"
                                : "IncludeWriteDep";
                        sb.AppendLine(
                            $"{body}{GenPrefix}deps = {GenPrefix}scheduler.{method}({GenPrefix}deps, {rid}, {e.HoistedSingleGroupLocal});"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeComponentRead:
                    case FromWorldFieldKind.NativeComponentWrite:
                    {
                        var rid = $"ResourceId.Component(ComponentTypeId<{t}>.Value)";
                        var method =
                            e.Kind == FromWorldFieldKind.NativeComponentRead
                                ? "IncludeReadDep"
                                : "IncludeWriteDep";
                        sb.AppendLine(
                            $"{body}{GenPrefix}deps = {GenPrefix}scheduler.{method}({GenPrefix}deps, {rid}, {e.ScheduleParamName}.GroupIndex);"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeComponentLookupRead:
                    case FromWorldFieldKind.NativeComponentLookupWrite:
                    {
                        var rid = $"ResourceId.Component(ComponentTypeId<{t}>.Value)";
                        var method =
                            e.Kind == FromWorldFieldKind.NativeComponentLookupRead
                                ? "IncludeReadDep"
                                : "IncludeWriteDep";
                        sb.AppendLine(
                            $"{body}foreach (var {GenPrefix}lg in {e.HoistedGroupsLocal})"
                        );
                        sb.AppendLine(
                            $"{body}    {GenPrefix}deps = {GenPrefix}scheduler.{method}({GenPrefix}deps, {rid}, {GenPrefix}lg);"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeFactory:
                    {
                        EmitNativeFactoryDeps(sb, body, e, isTracking: false);
                        break;
                    }
                    case FromWorldFieldKind.NativeSetCommandBuffer:
                    {
                        sb.AppendLine(
                            $"{body}{GenPrefix}deps = {GenPrefix}world.IncludeNativeSetCommandBufferDepsForJob<{t}>({GenPrefix}deps);"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeEntitySetIndices:
                    {
                        var rid = $"ResourceId.Set(EntitySet<{t}>.Value.Id)";
                        sb.AppendLine(
                            $"{body}{GenPrefix}deps = {GenPrefix}scheduler.IncludeReadDep({GenPrefix}deps, {rid}, {e.HoistedSingleGroupLocal});"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeSetRead:
                    {
                        sb.AppendLine(
                            $"{body}{GenPrefix}deps = {GenPrefix}world.IncludeNativeSetReadDepsForJob<{t}>({GenPrefix}deps);"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeWorldAccessor:
                    case FromWorldFieldKind.GroupIndex:
                    case FromWorldFieldKind.NativeEntityHandleBuffer:
                        // No dependency registration — these are passive identifiers.
                        break;
                }
            }
        }

        /// <summary>
        /// Emit per-(component, group) dep registration for a NativeFactory field.
        /// For each component in the aspect, emits IncludeReadDep/IncludeWriteDep
        /// (scheduling) or TrackJobRead/TrackJobWrite (tracking) inside a foreach
        /// over the factory's hoisted groups list.
        /// </summary>
        internal static void EmitNativeFactoryDeps(
            StringBuilder sb,
            string body,
            FromWorldFieldEmit e,
            bool isTracking
        )
        {
            var aspectData = e.AspectData;
            if (aspectData == null)
                return;

            sb.AppendLine($"{body}foreach (var {GenPrefix}lg in {e.HoistedGroupsLocal})");
            sb.AppendLine($"{body}{{");
            string innerBody = body + "    ";
            foreach (var comp in aspectData.ReadTypes)
            {
                var compName = PerformanceCache.GetDisplayString(comp);
                var rid = $"ResourceId.Component(ComponentTypeId<{compName}>.Value)";
                var method = isTracking ? "TrackJobRead" : "IncludeReadDep";
                if (isTracking)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {GenPrefix}lg);"
                    );
                else
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}scheduler.{method}({GenPrefix}deps, {rid}, {GenPrefix}lg);"
                    );
            }
            foreach (var comp in aspectData.WriteTypes)
            {
                var compName = PerformanceCache.GetDisplayString(comp);
                var rid = $"ResourceId.Component(ComponentTypeId<{compName}>.Value)";
                var method = isTracking ? "TrackJobWrite" : "IncludeWriteDep";
                if (isTracking)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {GenPrefix}lg);"
                    );
                else
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}scheduler.{method}({GenPrefix}deps, {rid}, {GenPrefix}lg);"
                    );
            }
            sb.AppendLine($"{body}}}");
        }

        /// <summary>
        /// Emit the field assignment for a NativeFactory field. Creates individual
        /// NativeComponentLookup instances for each aspect component, registers them for
        /// disposal, and constructs the NativeFactory from them.
        /// </summary>
        internal static void EmitNativeFactoryFieldAssignment(
            StringBuilder sb,
            string body,
            FromWorldFieldEmit e
        )
        {
            var aspectData = e.AspectData;
            if (aspectData == null)
                return;

            var allTypes = aspectData.AllComponentTypes;
            var lookupLocals = new List<string>(allTypes.Length);

            for (int i = 0; i < allTypes.Length; i++)
            {
                var compName = PerformanceCache.GetDisplayString(allTypes[i]);
                var isReadOnly =
                    aspectData.ReadTypes.Any(r =>
                        SymbolEqualityComparer.Default.Equals(r, allTypes[i])
                    )
                    && !aspectData.WriteTypes.Any(w =>
                        SymbolEqualityComparer.Default.Equals(w, allTypes[i])
                    );
                var createMethod = isReadOnly
                    ? "CreateNativeComponentLookupReadForJob"
                    : "CreateNativeComponentLookupWriteForJob";
                var local = $"{GenPrefix}factoryLookup_{e.FieldName}_{i}";
                lookupLocals.Add(local);

                sb.AppendLine(
                    $"{body}var {local} = {GenPrefix}world.{createMethod}<{compName}>({e.HoistedGroupsLocal}, Unity.Collections.Allocator.TempJob);"
                );
                sb.AppendLine($"{body}{GenPrefix}scheduler.RegisterPendingDispose({local});");
            }

            // Construct the NativeFactory from the individual lookups
            sb.Append($"{body}{GenPrefix}job.{e.FieldName} = new {e.GenericArgDisplay}(");
            for (int i = 0; i < lookupLocals.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(lookupLocals[i]);
            }
            sb.AppendLine(");");
        }

        /// <summary>
        /// Emits field value creation and assignment for [FromWorld] fields on the job instance.
        /// </summary>
        internal static void EmitFromWorldFieldAssignments(
            StringBuilder sb,
            string body,
            List<FromWorldFieldEmit> emits
        )
        {
            foreach (var e in emits)
            {
                var t = e.GenericArgDisplay;
                switch (e.Kind)
                {
                    case FromWorldFieldKind.NativeComponentBufferRead:
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {GenPrefix}world.GetBufferReadForJob<{t}>({e.HoistedSingleGroupLocal}).buffer;"
                        );
                        break;
                    case FromWorldFieldKind.NativeComponentBufferWrite:
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {GenPrefix}world.GetBufferWriteForJob<{t}>({e.HoistedSingleGroupLocal}).buffer;"
                        );
                        break;
                    case FromWorldFieldKind.NativeComponentRead:
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {GenPrefix}world.GetNativeComponentReadForJob<{t}>({e.ScheduleParamName});"
                        );
                        break;
                    case FromWorldFieldKind.NativeComponentWrite:
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {GenPrefix}world.GetNativeComponentWriteForJob<{t}>({e.ScheduleParamName});"
                        );
                        break;
                    case FromWorldFieldKind.NativeComponentLookupRead:
                        sb.AppendLine(
                            $"{body}var {GenPrefix}lookup_{e.FieldName} = {GenPrefix}world.CreateNativeComponentLookupReadForJob<{t}>({e.HoistedGroupsLocal}, Unity.Collections.Allocator.TempJob);"
                        );
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.RegisterPendingDispose({GenPrefix}lookup_{e.FieldName});"
                        );
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {GenPrefix}lookup_{e.FieldName};"
                        );
                        break;
                    case FromWorldFieldKind.NativeComponentLookupWrite:
                        sb.AppendLine(
                            $"{body}var {GenPrefix}lookup_{e.FieldName} = {GenPrefix}world.CreateNativeComponentLookupWriteForJob<{t}>({e.HoistedGroupsLocal}, Unity.Collections.Allocator.TempJob);"
                        );
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.RegisterPendingDispose({GenPrefix}lookup_{e.FieldName});"
                        );
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {GenPrefix}lookup_{e.FieldName};"
                        );
                        break;
                    case FromWorldFieldKind.NativeFactory:
                    {
                        EmitNativeFactoryFieldAssignment(sb, body, e);
                        break;
                    }
                    case FromWorldFieldKind.NativeSetCommandBuffer:
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {GenPrefix}world.CreateNativeSetCommandBufferForJob<{t}>();"
                        );
                        break;
                    case FromWorldFieldKind.NativeEntitySetIndices:
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {GenPrefix}world.GetSetIndicesForJob<{t}>({e.HoistedSingleGroupLocal});"
                        );
                        break;
                    case FromWorldFieldKind.NativeSetRead:
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {GenPrefix}world.CreateNativeSetReadForJob<{t}>();"
                        );
                        break;
                    case FromWorldFieldKind.NativeWorldAccessor:
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {GenPrefix}world.ToNative();"
                        );
                        break;
                    case FromWorldFieldKind.GroupIndex:
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {e.HoistedSingleGroupLocal};"
                        );
                        break;
                    case FromWorldFieldKind.NativeEntityHandleBuffer:
                        sb.AppendLine(
                            $"{body}{GenPrefix}job.{e.FieldName} = {GenPrefix}world.GetEntityHandleBufferForJob({e.HoistedSingleGroupLocal});"
                        );
                        break;
                }
            }
        }

        /// <summary>
        /// Emits post-schedule dependency tracking (TrackJobRead/TrackJobWrite) for [FromWorld] fields.
        /// </summary>
        internal static void EmitFromWorldTracking(
            StringBuilder sb,
            string body,
            List<FromWorldFieldEmit> emits
        )
        {
            foreach (var e in emits)
            {
                var t = e.GenericArgDisplay;
                switch (e.Kind)
                {
                    case FromWorldFieldKind.NativeComponentBufferRead:
                    case FromWorldFieldKind.NativeComponentBufferWrite:
                    {
                        var rid = $"ResourceId.Component(ComponentTypeId<{t}>.Value)";
                        var method =
                            e.Kind == FromWorldFieldKind.NativeComponentBufferRead
                                ? "TrackJobRead"
                                : "TrackJobWrite";
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {e.HoistedSingleGroupLocal});"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeComponentRead:
                    case FromWorldFieldKind.NativeComponentWrite:
                    {
                        var rid = $"ResourceId.Component(ComponentTypeId<{t}>.Value)";
                        var method =
                            e.Kind == FromWorldFieldKind.NativeComponentRead
                                ? "TrackJobRead"
                                : "TrackJobWrite";
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {e.ScheduleParamName}.GroupIndex);"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeComponentLookupRead:
                    case FromWorldFieldKind.NativeComponentLookupWrite:
                    {
                        var rid = $"ResourceId.Component(ComponentTypeId<{t}>.Value)";
                        var method =
                            e.Kind == FromWorldFieldKind.NativeComponentLookupRead
                                ? "TrackJobRead"
                                : "TrackJobWrite";
                        sb.AppendLine(
                            $"{body}foreach (var {GenPrefix}lg in {e.HoistedGroupsLocal})"
                        );
                        sb.AppendLine(
                            $"{body}    {GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {GenPrefix}lg);"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeFactory:
                    {
                        EmitNativeFactoryDeps(sb, body, e, isTracking: true);
                        break;
                    }
                    case FromWorldFieldKind.NativeSetCommandBuffer:
                        sb.AppendLine(
                            $"{body}{GenPrefix}world.TrackNativeSetCommandBufferDepsForJob<{t}>({GenPrefix}handle);"
                        );
                        break;
                    case FromWorldFieldKind.NativeEntitySetIndices:
                    {
                        var rid = $"ResourceId.Set(EntitySet<{t}>.Value.Id)";
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.TrackJobRead({GenPrefix}handle, {rid}, {e.HoistedSingleGroupLocal});"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeSetRead:
                        sb.AppendLine(
                            $"{body}{GenPrefix}world.TrackNativeSetReadDepsForJob<{t}>({GenPrefix}handle);"
                        );
                        break;
                    case FromWorldFieldKind.NativeWorldAccessor:
                        // NativeWorldAccessor performs structural operations (add/remove/move)
                        // that write to shared native queues. The job must complete before
                        // SubmitEntities processes those queues at the next phase boundary.
                        sb.AppendLine($"{body}{GenPrefix}scheduler.TrackJob({GenPrefix}handle);");
                        break;
                    case FromWorldFieldKind.GroupIndex:
                    case FromWorldFieldKind.NativeEntityHandleBuffer:
                        // No tracking — these are passive identifiers.
                        break;
                }
            }
        }
    }
}
