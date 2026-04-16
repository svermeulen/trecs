using System.Runtime.CompilerServices;
using Trecs.Collections;

namespace Trecs
{
    /// <summary>
    /// Iterates groups for dense queries (no set) and yields
    /// <see cref="DenseGroupSlice"/> per non-empty group.
    /// </summary>
    public ref struct DenseGroupSliceIterator
    {
        readonly WorldAccessor _world;
        readonly ReadOnlyFastList<Group> _validGroups;
        int _groupIndex;
        DenseGroupSlice _current;

        internal DenseGroupSliceIterator(WorldAccessor world, ReadOnlyFastList<Group> validGroups)
        {
            _world = world;
            _validGroups = validGroups;
            _groupIndex = -1;
            _current = default;
        }

        public readonly DenseGroupSlice Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _current;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DenseGroupSliceIterator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (++_groupIndex < _validGroups.Count)
            {
                var group = _validGroups[_groupIndex];
                var count = _world.CountEntitiesInGroup(group);

                if (count > 0)
                {
                    _current = new DenseGroupSlice(group, count);
                    return true;
                }
            }

            return false;
        }
    }
}
