using System.Runtime.CompilerServices;
using Trecs.Collections;

namespace Trecs
{
    /// <summary>
    /// Flat entity iterator that yields <see cref="EntityIndex"/> values for all entities
    /// matching a query. Supports dense (no set) and single-set sparse queries.
    /// </summary>
    /// <remarks>
    /// Self-enumerable — use directly in foreach:
    /// <code>foreach (var ei in queryBuilder.EntityIndices()) { ... }</code>
    /// </remarks>
    public ref struct QueryIterator
    {
        bool _done;
        Group _currentGroup;
        int _entityIndex;

        readonly WorldAccessor _world;
        readonly ReadOnlyFastList<Group> _groups;
        SetGroupLookup _singleSet;
        readonly bool _hasSet;
        int _groupIndex;
        int _sliceCount;
        EntitySetIndices _sliceIndices;
        int _slicePosition;

        /// <summary>No-set constructor — iterates all entities in every matched group.</summary>
        internal QueryIterator(WorldAccessor ecs, ReadOnlyFastList<Group> resolvedGroups)
        {
            _done = false;
            _currentGroup = default;
            _entityIndex = 0;
            _world = ecs;
            _groups = resolvedGroups;
            _groupIndex = -1;
            _sliceCount = 0;
            _slicePosition = 0;
            _sliceIndices = default;
            _hasSet = false;
            _singleSet = default;
        }

        /// <summary>Single-set constructor — iterates entities belonging to the given set.</summary>
        internal QueryIterator(WorldAccessor ecs, ReadOnlyFastList<Group> resolvedGroups, SetId set)
        {
            _done = false;
            _currentGroup = default;
            _entityIndex = 0;
            _world = ecs;
            _groups = resolvedGroups;
            _groupIndex = -1;
            _sliceCount = 0;
            _slicePosition = 0;
            _sliceIndices = default;
            _hasSet = true;
            _singleSet = ecs.GetSetGroupLookup(set);
        }

        public readonly EntityIndex Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new EntityIndex(_entityIndex, _currentGroup);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryIterator GetEnumerator() => this;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (_done)
                return false;

            _slicePosition++;

            if (_slicePosition < _sliceCount)
            {
                _entityIndex = _hasSet ? _sliceIndices[_slicePosition] : _slicePosition;
                return true;
            }

            return AdvanceToNextGroup();
        }

        bool AdvanceToNextGroup()
        {
            while (++_groupIndex < _groups.Count)
            {
                _currentGroup = _groups[_groupIndex];

                if (_hasSet)
                {
                    if (
                        !_singleSet.TryGetGroupEntry(_currentGroup, out var entry)
                        || entry.Count == 0
                    )
                        continue;

                    _sliceCount = entry.Count;
                    _sliceIndices = entry.Indices;
                }
                else
                {
                    var count = _world.CountEntitiesInGroup(_currentGroup);
                    if (count == 0)
                        continue;
                    _sliceCount = count;
                }

                _slicePosition = 0;
                _entityIndex = _hasSet ? _sliceIndices[0] : 0;
                return true;
            }

            _done = true;
            return false;
        }
    }
}
