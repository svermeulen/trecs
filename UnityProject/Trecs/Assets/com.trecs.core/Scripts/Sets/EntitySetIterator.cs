using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Unified set iterator. Yields (EntitySetIndices indices, GroupIndex group) per non-empty group.
    /// </summary>
    public ref struct EntitySetIterator
    {
        int _registeredIndex;
        readonly int _registeredCount;

        [NativeDisableContainerSafetyRestriction]
        readonly NativeList<SetGroupEntry> _entriesPerGroup;

        [NativeDisableContainerSafetyRestriction]
        readonly NativeList<GroupIndex> _registeredGroups;

        SetGroupEntry _current;

        internal EntitySetIterator(in EntitySet set)
            : this(set._entriesPerGroup, set._registeredGroups) { }

        internal EntitySetIterator(
            NativeList<SetGroupEntry> entriesPerGroup,
            NativeList<GroupIndex> registeredGroups
        )
        {
            _entriesPerGroup = entriesPerGroup;
            _registeredGroups = registeredGroups;
            _registeredCount = registeredGroups.Length;
            _registeredIndex = -1;
            _current = default;
        }

        public readonly RefCurrent Current => new(_current);

        public bool MoveNext()
        {
            while (++_registeredIndex < _registeredCount)
            {
                _current = _entriesPerGroup[_registeredGroups[_registeredIndex].Index];

                if (_current.Count > 0)
                    break;
            }

            return _registeredIndex < _registeredCount;
        }

        public readonly ref struct RefCurrent
        {
            readonly SetGroupEntry _entry;

            internal RefCurrent(SetGroupEntry entry)
            {
                _entry = entry;
            }

            public void Deconstruct(out EntitySetIndices indices, out GroupIndex group)
            {
                indices = _entry.Indices;
                group = _entry.GroupIndex;
            }

            public void Deconstruct(
                out EntitySetIndices indices,
                out GroupIndex group,
                out SetGroupEntryRead groupEntry
            )
            {
                indices = _entry.Indices;
                group = _entry.GroupIndex;
                groupEntry = new SetGroupEntryRead(_entry);
            }
        }
    }
}
