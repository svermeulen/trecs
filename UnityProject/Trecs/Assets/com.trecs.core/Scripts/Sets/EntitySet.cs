using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

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
    /// Storage layout: <see cref="_entriesPerGroup"/> is a full-size array indexed
    /// directly by <see cref="GroupIndex.Index"/>. Slots for groups that don't
    /// match this set's template are <c>default(SetGroupEntry)</c> (<c>IsValid == false</c>).
    /// <see cref="_registeredGroups"/> is a compact list of the subset of groups
    /// this set covers — used for iteration paths (dep-tracking, flush, dispose).
    /// </summary>
    internal readonly struct EntitySet
    {
        [NativeDisableContainerSafetyRestriction]
        internal readonly NativeList<SetGroupEntry> _entriesPerGroup;

        internal readonly NativeList<GroupIndex> _registeredGroups;

        internal readonly AtomicNativeBags _jobAddQueue;
        internal readonly AtomicNativeBags _jobRemoveQueue;
        readonly SetId _setId;

        internal EntitySet(SetId setId, int totalGroupCount, DenseHashSet<GroupIndex> validGroups)
        {
            _setId = setId;

            _entriesPerGroup = new NativeList<SetGroupEntry>(totalGroupCount, Allocator.Persistent);
            _entriesPerGroup.Resize(totalGroupCount, NativeArrayOptions.ClearMemory);

            _registeredGroups = new NativeList<GroupIndex>(validGroups.Count, Allocator.Persistent);

            _jobAddQueue = AtomicNativeBags.Create();
            _jobRemoveQueue = AtomicNativeBags.Create();

            // Populate only the slots whose groups match this set's template.
            // Unmatched slots remain default(SetGroupEntry) — IsValid == false.
            foreach (var group in validGroups)
            {
                _entriesPerGroup[group.Index] = new SetGroupEntry(group);
                _registeredGroups.Add(group);
            }
        }

        internal int RegisteredGroupCount => _registeredGroups.Length;

        public SetId SetId => _setId;

        public EntitySetIterator GetEnumerator() => new(this);

        // ── Immediate operations (main thread only) ────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddImmediate(EntityIndex entityIndex)
        {
            AssertValidGroup(entityIndex.GroupIndex);
            _entriesPerGroup[entityIndex.GroupIndex.Index].Add(entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveImmediate(EntityIndex entityIndex)
        {
            AssertValidGroup(entityIndex.GroupIndex);
            _entriesPerGroup[entityIndex.GroupIndex.Index].Remove(entityIndex.Index);
        }

        // ── Internal immediate operations (structural updates) ─────────
        //
        // Add and Remove handle null GroupIndex asymmetrically, preserving the
        // original dict semantics: the old dict's indexer threw on unknown key
        // (including null) so Add crashes on null; the old dict's TryGetValue
        // silently returned false, so Remove silently no-ops on null. Callers
        // of *Unchecked paths that enqueue bad entity indices (e.g. remove-
        // supersedes-move flush) rely on the silent Remove.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddImmediateUnchecked(EntityIndex entityIndex)
        {
            _entriesPerGroup[entityIndex.GroupIndex.Index].Add(entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveImmediateUnchecked(EntityIndex entityIndex)
        {
            var group = entityIndex.GroupIndex;
            if (group.IsNull)
                return;
            var entry = _entriesPerGroup[group.Index];
            if (entry.IsValid)
            {
                entry.Remove(entityIndex.Index);
            }
        }

        // ── Queries ────────────────────────────────────────────────────

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SetGroupEntry GetSetGroupEntry(GroupIndex group)
        {
            if (TryGetGroupEntry(group, out var groupEntry))
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
            for (int i = 0; i < _jobRemoveQueue.Count; i++)
            {
                ref var bag = ref _jobRemoveQueue.GetBag(i);
                while (!bag.IsEmpty())
                {
                    var entityIndex = bag.Dequeue<EntityIndex>();
                    _entriesPerGroup[entityIndex.GroupIndex.Index].Remove(entityIndex.Index);
                }
            }

            for (int i = 0; i < _jobAddQueue.Count; i++)
            {
                ref var bag = ref _jobAddQueue.GetBag(i);
                while (!bag.IsEmpty())
                {
                    var entityIndex = bag.Dequeue<EntityIndex>();
                    _entriesPerGroup[entityIndex.GroupIndex.Index].Add(entityIndex.Index);
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

            for (int i = 0; i < _registeredGroups.Length; i++)
            {
                _entriesPerGroup[_registeredGroups[i].Index].Clear();
            }
        }

        static void DrainBags(AtomicNativeBags bags)
        {
            for (int i = 0; i < bags.Count; i++)
            {
                ref var bag = ref bags.GetBag(i);
                while (!bag.IsEmpty())
                    bag.Dequeue<EntityIndex>();
            }
        }

        public int ComputeFinalCount()
        {
            int count = 0;
            for (int i = 0; i < _registeredGroups.Length; i++)
            {
                count += _entriesPerGroup[_registeredGroups[i].Index].Count;
            }
            return count;
        }

        public void Dispose()
        {
            for (int i = 0; i < _registeredGroups.Length; i++)
            {
                _entriesPerGroup[_registeredGroups[i].Index].Dispose();
            }

            _entriesPerGroup.Dispose();
            _registeredGroups.Dispose();
            _jobAddQueue.Dispose();
            _jobRemoveQueue.Dispose();
        }

        // ── Validation ─────────────────────────────────────────────────

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
