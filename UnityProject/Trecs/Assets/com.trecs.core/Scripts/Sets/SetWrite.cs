using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trecs.Internal;

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
        readonly NativeDenseDictionary<GroupIndex, SetGroupEntry> _entriesPerGroup;

        internal SetWrite(
            WorldAccessor world,
            SetId setId,
            NativeDenseDictionary<GroupIndex, SetGroupEntry> entriesPerGroup
        )
        {
            _world = world;
            _setId = setId;
            _entriesPerGroup = entriesPerGroup;
        }

        // ── Write operations ─────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddImmediate(EntityIndex entityIndex)
        {
            AssertValidGroup(entityIndex.GroupIndex);
            _entriesPerGroup[entityIndex.GroupIndex].Add(entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveImmediate(EntityIndex entityIndex)
        {
            AssertValidGroup(entityIndex.GroupIndex);
            _entriesPerGroup[entityIndex.GroupIndex].Remove(entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddImmediate(EntityHandle entityHandle)
        {
            AddImmediate(entityHandle.ToIndex(_world));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveImmediate(EntityHandle entityHandle)
        {
            RemoveImmediate(entityHandle.ToIndex(_world));
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
        public bool TryGetGroupEntry(GroupIndex group, out SetGroupEntry groupEntry)
        {
            return _entriesPerGroup.TryGetValue(group, out groupEntry);
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

        // ── Validation ───────────────────────────────────────────────────

        [Conditional("DEBUG")]
        void AssertValidGroup(GroupIndex group)
        {
#if DEBUG
            Assert.That(
                _entriesPerGroup.ContainsKey(group),
                "GroupIndex {} does not belong to this set's template",
                group
            );
#endif
        }
    }
}
