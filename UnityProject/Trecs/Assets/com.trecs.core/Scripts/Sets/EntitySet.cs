using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;

namespace Trecs
{
    /// <summary>
    /// Generic cache for SetDef values derived from IEntitySet struct types.
    /// Provides zero-allocation access to pre-computed SetDef instances.
    /// Works for all sets.
    /// </summary>
    public static class EntitySet<T>
        where T : struct, IEntitySet
    {
        public static readonly SetDef Value = SetFactory.CreateSet(typeof(T));
    }
}

namespace Trecs.Internal
{
    /// <summary>
    /// A collection of entity indices organized by group, representing a subset of entities
    /// that match some user-defined criteria.
    ///
    /// Supports immediate mutations via <see cref="AddImmediate"/>/<see cref="RemoveImmediate"/>
    /// (main thread only). Deferred mutations are handled externally via
    /// <see cref="WorldAccessor.SetAdd{T}"/> / <see cref="NativeWorldAccessor.SetAdd{TSet}"/>.
    ///
    /// All valid group entries are pre-populated at registration so <see cref="AddImmediate"/> never
    /// mutates the shared group dictionary, making concurrent job reads of different groups safe.
    /// </summary>
    internal readonly struct EntitySet
    {
        internal readonly NativeDenseDictionary<Group, SetGroupEntry> _entriesPerGroup;
        internal readonly AtomicNativeBags _jobAddQueue;
        internal readonly AtomicNativeBags _jobRemoveQueue;
        readonly SetId _setId;

        internal EntitySet(SetId setId, DenseHashSet<Group> validGroups)
        {
            _setId = setId;
            _entriesPerGroup = new NativeDenseDictionary<Group, SetGroupEntry>(
                (uint)validGroups.Count,
                Allocator.Persistent
            );
            _jobAddQueue = AtomicNativeBags.Create();
            _jobRemoveQueue = AtomicNativeBags.Create();

            // Pre-populate all valid group entries so AddImmediate() never mutates the
            // dictionary structure, making concurrent job reads of different groups safe.
            foreach (var group in validGroups)
            {
                _entriesPerGroup.Add(group, new SetGroupEntry(group));
            }
        }

        internal int GroupCount => _entriesPerGroup.Count;

        public SetId SetId => _setId;

        public EntitySetIterator GetEnumerator() => new(this);

        // ── Immediate operations (main thread only) ────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddImmediate(EntityIndex entityIndex)
        {
            AssertValidGroup(entityIndex.Group);
            _entriesPerGroup[entityIndex.Group].Add(entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveImmediate(EntityIndex entityIndex)
        {
            AssertValidGroup(entityIndex.Group);
            _entriesPerGroup[entityIndex.Group].Remove(entityIndex.Index);
        }

        // ── Internal immediate operations (structural updates) ─────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddImmediateUnchecked(EntityIndex entityIndex)
        {
            _entriesPerGroup[entityIndex.Group].Add(entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveImmediateUnchecked(EntityIndex entityIndex)
        {
            if (_entriesPerGroup.TryGetValue(entityIndex.Group, out var groupEntry))
            {
                groupEntry.Remove(entityIndex.Index);
            }
        }

        // ── Queries ────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(EntityIndex entityIndex)
        {
            if (_entriesPerGroup.TryGetValue(entityIndex.Group, out var groupEntry))
            {
                return groupEntry.Exists(entityIndex.Index);
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetGroupEntry(Group group, out SetGroupEntry groupEntry)
        {
            return _entriesPerGroup.TryGetValue(group, out groupEntry);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SetGroupEntry GetSetGroupEntry(Group group)
        {
            if (_entriesPerGroup.TryGetValue(group, out var groupEntry))
                return groupEntry;

            throw new TrecsException($"no set linked to group {group}");
        }

        // ── Job write support ──────────────────────────────────────────

        internal NativeSetWrite<TSet> CreateWriter<TSet>()
            where TSet : struct, IEntitySet => new(_jobAddQueue, _jobRemoveQueue);

        /// <summary>
        /// Drain all pending job writes into the actual group entries.
        /// Called on the main thread after outstanding writer jobs have completed.
        /// Removes are processed before adds so that re-add-after-remove works correctly.
        /// </summary>
        internal void FlushJobWrites()
        {
            for (int i = 0; i < _jobRemoveQueue.count; i++)
            {
                ref var bag = ref _jobRemoveQueue.GetBag(i);
                while (!bag.IsEmpty())
                {
                    var entityIndex = bag.Dequeue<EntityIndex>();
                    _entriesPerGroup[entityIndex.Group].Remove(entityIndex.Index);
                }
            }

            for (int i = 0; i < _jobAddQueue.count; i++)
            {
                ref var bag = ref _jobAddQueue.GetBag(i);
                while (!bag.IsEmpty())
                {
                    var entityIndex = bag.Dequeue<EntityIndex>();
                    _entriesPerGroup[entityIndex.Group].Add(entityIndex.Index);
                }
            }
        }

        // ── Bulk operations ────────────────────────────────────────────

        /// <summary>
        /// Clear all entries and drain pending job write queues.
        /// Deferred queues are drained separately by the caller.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            DrainBags(_jobAddQueue);
            DrainBags(_jobRemoveQueue);

            var groupEntries = _entriesPerGroup.GetValuesWrite(out var count);
            for (var i = 0; i < count; i++)
            {
                groupEntries[i].Clear();
            }
        }

        static void DrainBags(AtomicNativeBags bags)
        {
            for (int i = 0; i < bags.count; i++)
            {
                ref var bag = ref bags.GetBag(i);
                while (!bag.IsEmpty())
                    bag.Dequeue<EntityIndex>();
            }
        }

        public int ComputeFinalCount()
        {
            int count = 0;
            var groupEntries = _entriesPerGroup.GetValuesRead(out var groupCount);
            for (int i = 0; i < groupCount; i++)
            {
                count += groupEntries[i].Count;
            }
            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SetGroupEntry GetGroup(int indexGroup)
        {
            Assert.That(indexGroup < _entriesPerGroup.Count);
            return _entriesPerGroup.GetValuesWrite(out _)[indexGroup];
        }

        public void Dispose()
        {
            var groupEntries = _entriesPerGroup.GetValuesWrite(out var count);
            for (var i = 0; i < count; i++)
            {
                groupEntries[i].Dispose();
            }

            _entriesPerGroup.Dispose();
            _jobAddQueue.Dispose();
            _jobRemoveQueue.Dispose();
        }

        // ── Validation ─────────────────────────────────────────────────

        [System.Diagnostics.Conditional("DEBUG")]
        void AssertValidGroup(Group group)
        {
#if DEBUG
            Assert.That(
                _entriesPerGroup.ContainsKey(group),
                "Group {} does not belong to this set's template",
                group
            );
#endif
        }
    }
}
