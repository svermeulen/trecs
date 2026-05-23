#nullable enable

using System.Text;

namespace Trecs.SourceGen.Shared
{
    internal static class PerGroupHiddenFieldEmitter
    {
        /// <summary>
        /// Emits the conditional declarations of per-group hidden fields onto the generated
        /// job struct. Called from each generator's job-struct emitter.
        ///
        /// `visibility` is the access modifier emitted on each field — typically `"private"`
        /// when the schedule method is generated into the same struct as the fields, and
        /// `"internal"` when the schedule method lives in a separate containing type and must
        /// reach in to assign the fields.
        /// </summary>
        internal static void EmitDeclarations(
            StringBuilder sb,
            string indent,
            string visibility,
            bool needsGroupField,
            bool needsGlobalIndexOffset,
            bool hasNativeWorldAccessor,
            bool needsEntityHandleBuffer
        )
        {
            var f = FromWorldEmitter.JobFieldPrefix;

            // GroupIndex — needed for aspect (for EntityIndex ctor) or for components when an
            // EntityIndex parameter is present.
            if (needsGroupField)
            {
                sb.AppendLine($"{indent}{GeneratedCodeAttributes.Line}");
                sb.AppendLine($"{indent}{visibility} GroupIndex {f}GroupIndex;");
            }

            // GlobalIndexOffset — the per-call offset assigned per group by the schedule
            // overload; the Execute shim adds it to `i` before forwarding.
            if (needsGlobalIndexOffset)
            {
                sb.AppendLine($"{indent}{GeneratedCodeAttributes.Line}");
                sb.AppendLine($"{indent}{visibility} int {f}GlobalIndexOffset;");
            }

            // NativeWorldAccessor.
            if (hasNativeWorldAccessor)
            {
                sb.AppendLine($"{indent}{GeneratedCodeAttributes.Line}");
                sb.AppendLine($"{indent}{visibility} NativeWorldAccessor {f}nwa;");
            }

            // NativeEntityHandleBuffer — populated per-group at schedule time so the Execute
            // shim can materialize the user's EntityHandle parameter as `__EntityHandles[i]`
            // (no dictionary lookup, just an indexed read).
            if (needsEntityHandleBuffer)
            {
                sb.AppendLine($"{indent}{GeneratedCodeAttributes.Line}");
                sb.AppendLine($"{indent}{visibility} NativeEntityHandleBuffer {f}EntityHandles;");
            }
        }

        /// <summary>
        /// Emits the conditional assignments of per-group hidden fields onto the per-iteration
        /// job copy. Called from both schedule paths (dense + sparse) of both job generators
        /// after `{prefix}job` is in scope and after iteration buffers have been assigned.
        /// Caller must have `{prefix}job`, `{prefix}group`, `{prefix}queryIndexOffset`, and
        /// `{prefix}world` in scope per the surrounding emit contract.
        /// </summary>
        internal static void EmitAssignments(
            StringBuilder sb,
            string indent,
            bool needsGroupField,
            bool needsGlobalIndexOffset,
            bool hasNativeWorldAccessor,
            bool needsEntityHandleBuffer
        )
        {
            // GenPrefix targets are *locals* in the surrounding emit scope (job, group,
            // world, queryIndexOffset). JobFieldPrefix targets are *fields* on the
            // generated job struct (GroupIndex, GlobalIndexOffset, nwa, EntityHandles).
            var p = FromWorldEmitter.GenPrefix;
            var f = FromWorldEmitter.JobFieldPrefix;

            if (needsGroupField)
                sb.AppendLine($"{indent}{p}job.{f}GroupIndex = {p}group;");

            if (needsGlobalIndexOffset)
                sb.AppendLine($"{indent}{p}job.{f}GlobalIndexOffset = {p}queryIndexOffset;");

            if (hasNativeWorldAccessor)
                sb.AppendLine($"{indent}{p}job.{f}nwa = {p}world.ToNative();");

            if (needsEntityHandleBuffer)
                sb.AppendLine(
                    $"{indent}{p}job.{f}EntityHandles = {p}world.GetEntityHandleBufferForJob({p}group);"
                );
        }
    }
}
