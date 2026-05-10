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
            var p = FromWorldEmitter.GenPrefix;

            // GroupIndex — needed for aspect (for EntityIndex ctor) or for components when an
            // EntityIndex parameter is present.
            if (needsGroupField)
                sb.AppendLine($"{indent}{visibility} GroupIndex {p}GroupIndex;");

            // GlobalIndexOffset — the per-call offset assigned per group by the schedule
            // overload; the Execute shim adds it to `i` before forwarding.
            if (needsGlobalIndexOffset)
                sb.AppendLine($"{indent}{visibility} int {p}GlobalIndexOffset;");

            // NativeWorldAccessor.
            if (hasNativeWorldAccessor)
                sb.AppendLine($"{indent}{visibility} NativeWorldAccessor {p}nwa;");

            // NativeEntityHandleBuffer — populated per-group at schedule time so the Execute
            // shim can materialize the user's EntityHandle parameter as `_trecs_EntityHandles[i]`
            // (no dictionary lookup, just an indexed read).
            if (needsEntityHandleBuffer)
                sb.AppendLine($"{indent}{visibility} NativeEntityHandleBuffer {p}EntityHandles;");
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
            var p = FromWorldEmitter.GenPrefix;

            if (needsGroupField)
                sb.AppendLine($"{indent}{p}job.{p}GroupIndex = {p}group;");

            if (needsGlobalIndexOffset)
                sb.AppendLine($"{indent}{p}job.{p}GlobalIndexOffset = {p}queryIndexOffset;");

            if (hasNativeWorldAccessor)
                sb.AppendLine($"{indent}{p}job.{p}nwa = {p}world.ToNative();");

            if (needsEntityHandleBuffer)
                sb.AppendLine(
                    $"{indent}{p}job.{p}EntityHandles = {p}world.GetEntityHandleBufferForJob({p}group);"
                );
        }
    }
}
