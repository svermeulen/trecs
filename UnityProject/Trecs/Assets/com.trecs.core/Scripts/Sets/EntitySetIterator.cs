using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Unified set iterator. Yields (EntitySetIndices indices, GroupIndex group) per non-empty group.
    /// </summary>
    public ref struct EntitySetIterator
    {
        int _indexGroup;
        readonly int _groupCount;
        readonly NativeDenseDictionary<GroupIndex, SetGroupEntry> _entriesPerGroup;
        SetGroupEntry _current;

        internal EntitySetIterator(in EntitySet set)
            : this(set._entriesPerGroup) { }

        internal EntitySetIterator(NativeDenseDictionary<GroupIndex, SetGroupEntry> entriesPerGroup)
        {
            _entriesPerGroup = entriesPerGroup;
            _groupCount = _entriesPerGroup.Count;
            _indexGroup = -1;
            _current = default;
        }

        public readonly RefCurrent Current => new(_current);

        public bool MoveNext()
        {
            while (++_indexGroup < _groupCount)
            {
                _current = _entriesPerGroup.GetValuesWrite(out _)[_indexGroup];

                if (_current.Count > 0)
                    break;
            }

            return _indexGroup < _groupCount;
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
