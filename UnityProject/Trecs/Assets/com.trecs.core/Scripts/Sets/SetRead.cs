using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Read-only set view returned by <see cref="SetAccessor{T}.Read"/>.
    /// The appropriate sync has already been performed — all operations go
    /// directly to the cached native data with no per-call sync or dictionary lookup.
    /// </summary>
    public readonly ref struct SetRead<T>
        where T : struct, IEntitySet
    {
        readonly WorldAccessor _world;
        readonly NativeDenseDictionary<GroupIndex, SetGroupEntry> _entriesPerGroup;

        internal SetRead(
            WorldAccessor world,
            NativeDenseDictionary<GroupIndex, SetGroupEntry> entriesPerGroup
        )
        {
            _world = world;
            _entriesPerGroup = entriesPerGroup;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(EntityIndex entityIndex)
        {
            if (_entriesPerGroup.TryGetValue(entityIndex.GroupIndex, out var groupEntry))
                return groupEntry.Exists(entityIndex.Index);
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(EntityHandle entityHandle)
        {
            return Exists(entityHandle.ToIndex(_world));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetGroupEntry(GroupIndex group, out SetGroupEntryRead groupEntry)
        {
            if (_entriesPerGroup.TryGetValue(group, out var entry))
            {
                groupEntry = new SetGroupEntryRead(entry);
                return true;
            }
            groupEntry = default;
            return false;
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int count = 0;
                var groupEntries = _entriesPerGroup.GetValuesRead(out var groupCount);
                for (int i = 0; i < groupCount; i++)
                    count += groupEntries[i].Count;
                return count;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntitySetIterator GetEnumerator()
        {
            return new EntitySetIterator(_entriesPerGroup);
        }
    }
}
