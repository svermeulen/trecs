namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Emits the per-iteration local declarations for the entity-shaped
    /// <c>[ForEachEntity]</c> parameter types: <c>EntityIndex</c>,
    /// <c>EntityHandle</c>, and <c>EntityAccessor</c>.
    /// <para>
    /// Shared between <c>ForEachGenerator</c> (components mode) and
    /// <c>ForEachEntityAspectGenerator</c> (aspect mode) — both call the same
    /// helper from each of their dense / sparse / range iteration paths so the
    /// emitted variable names and order stay consistent.
    /// </para>
    /// </summary>
    internal static class EntityRefEmitter
    {
        /// <summary>
        /// Emits up to three locals at the top of the loop body, named
        /// <c>__entityIndex</c>, <c>__entityHandle</c>, <c>__entityAccessor</c>,
        /// matching the slot kinds the user took. <c>__entityIndex</c> is the
        /// shared root and is emitted whenever a handle or accessor is requested
        /// (because both derive from it).
        /// </summary>
        /// <param name="indexExpr">The expression that constructs the per-iter
        /// <c>EntityIndex</c>, e.g. <c>"new EntityIndex(__i, __slice.GroupIndex)"</c>.
        /// Differs across the dense / sparse / range loops.</param>
        /// <param name="worldVar">The in-scope <c>WorldAccessor</c> variable name
        /// used to resolve the handle / accessor (e.g. <c>"__world"</c> or the
        /// user's named convenience-overload parameter).</param>
        public static void EmitDeclarations(
            OptimizedStringBuilder sb,
            ValidatedMethodInfo info,
            int indentLevel,
            string indexExpr,
            string worldVar
        )
        {
            bool needsIndex =
                info.HasEntityIndexParameter
                || info.HasEntityHandleParameter
                || info.HasEntityAccessorParameter;
            if (needsIndex)
                sb.AppendLine(indentLevel, $"var __entityIndex = {indexExpr};");
            if (info.HasEntityHandleParameter)
                sb.AppendLine(
                    indentLevel,
                    $"var __entityHandle = {worldVar}.GetEntityHandle(__entityIndex);"
                );
            if (info.HasEntityAccessorParameter)
                sb.AppendLine(
                    indentLevel,
                    $"var __entityAccessor = {worldVar}.Entity(__entityIndex);"
                );
        }
    }
}
