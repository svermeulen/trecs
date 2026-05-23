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
        /// <summary>
        /// Prefix applied to generator-internal locals and other names that only appear
        /// inside generated method bodies the user never sees. Double-underscore signals
        /// "framework-internal" while leaving room for the shorter <see cref="JobFieldPrefix"/>
        /// on names users can encounter in the debugger or by reading .g.cs.
        /// </summary>
        internal const string GenPrefix = "__trecs_";

        /// <summary>
        /// Prefix applied to fields and nested types declared on generated job structs
        /// (e.g. <c>__pt_{Name}</c>, <c>__fw_{Name}</c>, <c>__GroupIndex</c>,
        /// <c>__buf{i}</c>, <c>__SparseShim</c>). The job struct itself is nested inside
        /// the user's class, so its members are more likely to surface in the debugger
        /// or in generated source the user inspects — kept short and prefix-free of the
        /// "trecs" token to reduce line noise there.
        /// </summary>
        internal const string JobFieldPrefix = "__";

        /// <summary>
        /// Emits TagSet resolution and group hoisting for [FromWorld] fields before the iteration loop.
        /// </summary>
        internal static void EmitFromWorldHoistedSetup(
            StringBuilder sb,
            string body,
            List<FromWorldFieldEmitModel> emits
        )
        {
            foreach (var e in emits)
            {
                if (e.InlineTagSetExpression.Length > 0)
                {
                    sb.AppendLine($"{body}var {e.TagSetExpression} = {e.InlineTagSetExpression};");
                    if (e.HasScheduleParam)
                    {
                        sb.AppendLine(
                            $"{body}if ({e.ScheduleParamName} != null) {e.TagSetExpression} = {e.TagSetExpression}.CombineWith({e.ScheduleParamName}.Value);"
                        );
                    }
                }
            }

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

        internal static void EmitFromWorldDepRegistration(
            StringBuilder sb,
            string body,
            List<FromWorldFieldEmitModel> emits
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
                        var rid = $"ResourceId.Component(TypeId<{t}>.Value)";
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
                        var rid = $"ResourceId.Component(TypeId<{t}>.Value)";
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
                        var rid = $"ResourceId.Component(TypeId<{t}>.Value)";
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
                        break;
                }
            }
        }

        internal static void EmitNativeFactoryDeps(
            StringBuilder sb,
            string body,
            in FromWorldFieldEmitModel e,
            bool isTracking
        )
        {
            if (!e.HasAspectData)
                return;
            var aspectData = e.AspectData;

            sb.AppendLine($"{body}foreach (var {GenPrefix}lg in {e.HoistedGroupsLocal})");
            sb.AppendLine($"{body}{{");
            string innerBody = body + "    ";
            foreach (var compName in aspectData.ReadTypeDisplays)
            {
                var rid = $"ResourceId.Component(TypeId<{compName}>.Value)";
                var method = isTracking ? "TrackJobRead" : "IncludeReadDep";
                if (isTracking)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {GenPrefix}lg, {GenPrefix}jobName);"
                    );
                else
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}scheduler.{method}({GenPrefix}deps, {rid}, {GenPrefix}lg);"
                    );
            }
            foreach (var compName in aspectData.WriteTypeDisplays)
            {
                var rid = $"ResourceId.Component(TypeId<{compName}>.Value)";
                var method = isTracking ? "TrackJobWrite" : "IncludeWriteDep";
                if (isTracking)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {GenPrefix}lg, {GenPrefix}jobName);"
                    );
                else
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}scheduler.{method}({GenPrefix}deps, {rid}, {GenPrefix}lg);"
                    );
            }
            sb.AppendLine($"{body}}}");
        }

        internal static void EmitNativeFactoryFieldAssignment(
            StringBuilder sb,
            string body,
            in FromWorldFieldEmitModel e
        )
        {
            if (!e.HasAspectData)
                return;
            var aspectData = e.AspectData;

            var allTypes = aspectData.AllComponentDisplays;
            var lookupLocals = new List<string>(allTypes.Length);

            for (int i = 0; i < allTypes.Length; i++)
            {
                var compName = allTypes[i];
                // A component is read-only iff it appears in reads and NOT in writes.
                // The model's IsWrite check covers the "in writes" half; the rest of
                // AllComponentDisplays must be reads-only by construction.
                var isReadOnly = !aspectData.IsWrite(compName);
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

            sb.Append($"{body}{GenPrefix}job.{e.FieldName} = new {e.GenericArgDisplay}(");
            for (int i = 0; i < lookupLocals.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(lookupLocals[i]);
            }
            sb.AppendLine(");");
        }

        internal static void EmitFromWorldFieldAssignments(
            StringBuilder sb,
            string body,
            List<FromWorldFieldEmitModel> emits
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

        internal static void EmitFromWorldTracking(
            StringBuilder sb,
            string body,
            List<FromWorldFieldEmitModel> emits
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
                        var rid = $"ResourceId.Component(TypeId<{t}>.Value)";
                        var method =
                            e.Kind == FromWorldFieldKind.NativeComponentBufferRead
                                ? "TrackJobRead"
                                : "TrackJobWrite";
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {e.HoistedSingleGroupLocal}, {GenPrefix}jobName);"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeComponentRead:
                    case FromWorldFieldKind.NativeComponentWrite:
                    {
                        var rid = $"ResourceId.Component(TypeId<{t}>.Value)";
                        var method =
                            e.Kind == FromWorldFieldKind.NativeComponentRead
                                ? "TrackJobRead"
                                : "TrackJobWrite";
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {e.ScheduleParamName}.GroupIndex, {GenPrefix}jobName);"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeComponentLookupRead:
                    case FromWorldFieldKind.NativeComponentLookupWrite:
                    {
                        var rid = $"ResourceId.Component(TypeId<{t}>.Value)";
                        var method =
                            e.Kind == FromWorldFieldKind.NativeComponentLookupRead
                                ? "TrackJobRead"
                                : "TrackJobWrite";
                        sb.AppendLine(
                            $"{body}foreach (var {GenPrefix}lg in {e.HoistedGroupsLocal})"
                        );
                        sb.AppendLine(
                            $"{body}    {GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {GenPrefix}lg, {GenPrefix}jobName);"
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
                            $"{body}{GenPrefix}scheduler.TrackJobRead({GenPrefix}handle, {rid}, {e.HoistedSingleGroupLocal}, {GenPrefix}jobName);"
                        );
                        break;
                    }
                    case FromWorldFieldKind.NativeSetRead:
                        sb.AppendLine(
                            $"{body}{GenPrefix}world.TrackNativeSetReadDepsForJob<{t}>({GenPrefix}handle);"
                        );
                        break;
                    case FromWorldFieldKind.NativeWorldAccessor:
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.TrackJob({GenPrefix}handle, {GenPrefix}jobName);"
                        );
                        break;
                    case FromWorldFieldKind.GroupIndex:
                    case FromWorldFieldKind.NativeEntityHandleBuffer:
                        break;
                }
            }
        }
    }
}
