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
        const string GenPrefix = FromWorldEmitter.GenPrefix;

        static string EiLocal(in SingleEntityEmitTargetModel e) =>
            $"{GenPrefix}se_{e.LocalNameRoot}_ei";

        static string WithTagsClause(in SingleEntityEmitTargetModel e) =>
            $"WithTags<{string.Join(", ", e.TagTypeDisplays)}>()";

        internal static void EmitHoistedSetup(
            StringBuilder sb,
            string body,
            IEnumerable<SingleEntityEmitTargetModel> targets
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

        internal static void EmitDepRegistration(
            StringBuilder sb,
            string body,
            IEnumerable<SingleEntityEmitTargetModel> targets
        )
        {
            foreach (var e in targets)
            {
                var ei = EiLocal(e);
                if (e.IsAspect)
                {
                    var aspectData = e.AspectData;
                    foreach (var n in aspectData.ReadTypeDisplays)
                    {
                        sb.AppendLine(
                            $"{body}{GenPrefix}deps = {GenPrefix}scheduler.IncludeReadDep({GenPrefix}deps, ResourceId.Component(TypeId<{n}>.Value), {ei}.GroupIndex);"
                        );
                    }
                    foreach (var n in aspectData.WriteTypeDisplays)
                    {
                        sb.AppendLine(
                            $"{body}{GenPrefix}deps = {GenPrefix}scheduler.IncludeWriteDep({GenPrefix}deps, ResourceId.Component(TypeId<{n}>.Value), {ei}.GroupIndex);"
                        );
                    }
                }
                else
                {
                    var method = e.IsComponentWrite ? "IncludeWriteDep" : "IncludeReadDep";
                    sb.AppendLine(
                        $"{body}{GenPrefix}deps = {GenPrefix}scheduler.{method}({GenPrefix}deps, ResourceId.Component(TypeId<{e.ComponentTypeDisplay}>.Value), {ei}.GroupIndex);"
                    );
                }
            }
        }

        internal static void EmitFieldAssignment(
            StringBuilder sb,
            string body,
            IEnumerable<SingleEntityEmitTargetModel> targets
        )
        {
            foreach (var e in targets)
            {
                var ei = EiLocal(e);
                if (e.IsAspect)
                {
                    var aspectData = e.AspectData;
                    var allComponents = aspectData.AllComponentDisplays;
                    var bufferLocals = new List<string>(allComponents.Length);
                    for (int i = 0; i < allComponents.Length; i++)
                    {
                        var compType = allComponents[i];
                        bool isWrite = aspectData.IsWrite(compType);
                        var ext = isWrite ? "GetBufferWriteForJob" : "GetBufferReadForJob";
                        var local = $"{GenPrefix}se_{e.LocalNameRoot}_b{i}";
                        bufferLocals.Add(local);
                        sb.AppendLine(
                            $"{body}var ({local}, _) = {GenPrefix}world.{ext}<{compType}>({ei}.GroupIndex);"
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

        internal static void EmitTracking(
            StringBuilder sb,
            string body,
            IEnumerable<SingleEntityEmitTargetModel> targets
        )
        {
            foreach (var e in targets)
            {
                var ei = EiLocal(e);
                if (e.IsAspect)
                {
                    var aspectData = e.AspectData;
                    foreach (var n in aspectData.ReadTypeDisplays)
                    {
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.TrackJobRead({GenPrefix}handle, ResourceId.Component(TypeId<{n}>.Value), {ei}.GroupIndex, {GenPrefix}jobName);"
                        );
                    }
                    foreach (var n in aspectData.WriteTypeDisplays)
                    {
                        sb.AppendLine(
                            $"{body}{GenPrefix}scheduler.TrackJobWrite({GenPrefix}handle, ResourceId.Component(TypeId<{n}>.Value), {ei}.GroupIndex, {GenPrefix}jobName);"
                        );
                    }
                }
                else
                {
                    var method = e.IsComponentWrite ? "TrackJobWrite" : "TrackJobRead";
                    sb.AppendLine(
                        $"{body}{GenPrefix}scheduler.{method}({GenPrefix}handle, ResourceId.Component(TypeId<{e.ComponentTypeDisplay}>.Value), {ei}.GroupIndex, {GenPrefix}jobName);"
                    );
                }
            }
        }
    }
}
