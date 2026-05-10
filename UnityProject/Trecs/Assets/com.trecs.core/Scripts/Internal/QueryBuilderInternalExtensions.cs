using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    /// <summary>
    /// Extension methods that expose <see cref="EntityIndex"/>-returning
    /// query terminators to source-generated code in user assemblies. User
    /// code should use the <see cref="QueryBuilder.Single"/> /
    /// <see cref="QueryBuilder.TrySingle"/> /
    /// <see cref="QueryBuilder.Entities"/> /
    /// <see cref="QueryBuilder.EntityHandles"/> overloads instead.
    /// </summary>
    public static class QueryBuilderInternalExtensions
    {
        // ── QueryBuilder ──────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityIndex SingleEntityIndex(this QueryBuilder builder) =>
            builder.SingleEntityIndex();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySingleEntityIndex(
            this QueryBuilder builder,
            out EntityIndex entityIndex
        ) => builder.TrySingleEntityIndex(out entityIndex);

        // ── SparseQueryBuilder ────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityIndex SingleEntityIndex(this SparseQueryBuilder builder) =>
            builder.SingleEntityIndex();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySingleEntityIndex(
            this SparseQueryBuilder builder,
            out EntityIndex entityIndex
        ) => builder.TrySingleEntityIndex(out entityIndex);
    }
}
