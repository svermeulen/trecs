using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

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

        [NativeDisableContainerSafetyRestriction]
        readonly NativeList<SetGroupEntry> _entriesPerGroup;

        readonly NativeList<GroupIndex> _registeredGroups;

        internal SetRead(WorldAccessor world, in EntitySet set)
        {
            _world = world;
            _entriesPerGroup = set._entriesPerGroup;
            _registeredGroups = set._registeredGroups;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(EntityIndex entityIndex)
        {
            var group = entityIndex.GroupIndex;
            if (group.IsNull)
                return false;
            var entry = _entriesPerGroup[group.Index];
            return entry.IsValid && entry.Exists(entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(EntityHandle entityHandle)
        {
            return Exists(entityHandle.ToIndex(_world));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetGroupEntry(GroupIndex group, out SetGroupEntryRead groupEntry)
        {
            if (group.IsNull)
            {
                groupEntry = default;
                return false;
            }
            var entry = _entriesPerGroup[group.Index];
            if (entry.IsValid)
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
                for (int i = 0; i < _registeredGroups.Length; i++)
                {
                    count += _entriesPerGroup[_registeredGroups[i].Index].Count;
                }
                return count;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntitySetIterator GetEnumerator()
        {
            return new EntitySetIterator(_entriesPerGroup, _registeredGroups);
        }
    }
}
