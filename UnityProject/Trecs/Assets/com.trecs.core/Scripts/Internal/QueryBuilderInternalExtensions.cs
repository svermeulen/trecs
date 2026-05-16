using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    /// <summary>
    /// Extension methods that expose <see cref="EntityIndex"/>-returning
    /// query terminators to source-generated code in user assemblies. User
    /// code should use the <see cref="QueryBuilder.SingleHandle"/> /
    /// <see cref="QueryBuilder.TrySingleHandle"/> /
    /// <see cref="QueryBuilder.Handles"/> overloads instead.
    /// </summary>
    public static class QueryBuilderInternalExtensions
    {
        // ── QueryBuilder ──────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityIndex SingleIndex(this QueryBuilder builder) => builder.SingleIndex();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySingleIndex(this QueryBuilder builder, out EntityIndex entityIndex) =>
            builder.TrySingleIndex(out entityIndex);

        // ── SparseQueryBuilder ────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityIndex SingleIndex(this SparseQueryBuilder builder) =>
            builder.SingleIndex();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySingleIndex(
            this SparseQueryBuilder builder,
            out EntityIndex entityIndex
        ) => builder.TrySingleIndex(out entityIndex);
    }
}
