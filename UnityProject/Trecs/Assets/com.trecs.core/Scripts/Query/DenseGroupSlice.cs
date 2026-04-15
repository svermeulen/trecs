using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// A per-group view for dense queries (no set). All entities in the group
    /// match the query, so iteration is a simple 0..Count loop.
    /// </summary>
    public readonly ref struct DenseGroupSlice
    {
        public readonly Group Group;
        public readonly int Count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal DenseGroupSlice(Group group, int count)
        {
            Group = group;
            Count = count;
        }
    }
}
