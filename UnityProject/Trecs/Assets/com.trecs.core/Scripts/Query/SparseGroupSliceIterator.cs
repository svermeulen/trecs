using System.Runtime.CompilerServices;
using Trecs.Collections;

namespace Trecs
{
    /// <summary>
    /// Iterates groups for a sparse query (with a single set) and yields
    /// a <see cref="SparseGroupSlice"/> per group that has at least one entity
    /// in the set.
    /// </summary>
    public ref struct SparseGroupSliceIterator
    {
        readonly ReadOnlyFastList<Group> _validGroups;
        SetGroupLookup _set;
        int _groupIndex;
        SparseGroupSlice _current;

        internal SparseGroupSliceIterator(
            WorldAccessor ecs,
            ReadOnlyFastList<Group> validGroups,
            SetId set
        )
        {
            _validGroups = validGroups;
            _set = ecs.GetSetGroupLookup(set);
            _groupIndex = -1;
            _current = default;
        }

        public readonly SparseGroupSlice Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _current;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SparseGroupSliceIterator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (++_groupIndex < _validGroups.Count)
            {
                var group = _validGroups[_groupIndex];

                if (_set.TryGetGroupEntry(group, out var entry) && entry.Count > 0)
                {
                    _current = new SparseGroupSlice(group, entry.Indices);
                    return true;
                }
            }

            return false;
        }
    }
}
