using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// A per-group view for sparse queries (with a set). Only entities in the
    /// applied set are yielded. Use <see cref="Indices"/> to iterate or index
    /// into component buffers.
    /// </summary>
    public readonly ref struct SparseGroupSlice
    {
        public readonly GroupIndex GroupIndex;
        public readonly EntitySetIndices Indices;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SparseGroupSlice(GroupIndex group, EntitySetIndices indices)
        {
            GroupIndex = group;
            Indices = indices;
        }
    }
}
