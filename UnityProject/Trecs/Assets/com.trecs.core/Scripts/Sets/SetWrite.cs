using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Read+write set view returned by <see cref="SetAccessor{T}.Write"/>.
    /// The appropriate sync has already been performed — all operations go
    /// directly to the cached native data with no per-call sync or dictionary lookup.
    /// </summary>
    public readonly ref struct SetWrite<T>
        where T : struct, IEntitySet
    {
        readonly WorldAccessor _world;
        readonly SetId _setId;

        [NativeDisableContainerSafetyRestriction]
        readonly NativeList<SetGroupEntry> _entriesPerGroup;

        readonly NativeList<GroupIndex> _registeredGroups;

        internal SetWrite(WorldAccessor world, SetId setId, in EntitySetStorage set)
        {
            _world = world;
            _setId = setId;
            _entriesPerGroup = set._entriesPerGroup;
            _registeredGroups = set._registeredGroups;
        }

        // ── Write operations ─────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(EntityIndex entityIndex)
        {
            AssertValidGroup(entityIndex.GroupIndex);
            _entriesPerGroup[entityIndex.GroupIndex.Index].Add(entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(EntityIndex entityIndex)
        {
            AssertValidGroup(entityIndex.GroupIndex);
            _entriesPerGroup[entityIndex.GroupIndex.Index].Remove(entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(EntityHandle entityHandle)
        {
            Add(entityHandle.ToIndex(_world));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(EntityHandle entityHandle)
        {
            Remove(entityHandle.ToIndex(_world));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _world.ClearSet(_setId);
        }

        // ── Read operations (write-sync is a superset of read-sync) ──────

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
        public bool TryGetGroupEntry(GroupIndex group, out SetGroupEntry groupEntry)
        {
            if (group.IsNull)
            {
                groupEntry = default;
                return false;
            }
            groupEntry = _entriesPerGroup[group.Index];
            return groupEntry.IsValid;
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

        // ── Validation ───────────────────────────────────────────────────

        [Conditional("DEBUG")]
        void AssertValidGroup(GroupIndex group)
        {
#if DEBUG
            Assert.That(
                !group.IsNull && _entriesPerGroup[group.Index].IsValid,
                "GroupIndex {} does not belong to this set's template",
                group
            );
#endif
        }
    }
}
