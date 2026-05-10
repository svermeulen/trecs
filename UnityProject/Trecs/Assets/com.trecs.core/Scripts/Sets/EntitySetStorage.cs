using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    /// <summary>
    /// A collection of entity indices organized by group, representing a subset of entities
    /// that match some user-defined criteria.
    ///
    /// Supports immediate structural mutations via <see cref="AddUnchecked"/>/<see cref="RemoveUnchecked"/>
    /// (main thread only, used during submission). User-facing immediate mutations go through
    /// <see cref="SetWrite{T}"/>; deferred mutations are handled externally via
    /// <see cref="SetAccessor{T}.Defer"/> / <see cref="NativeWorldAccessor.SetAdd{TSet}"/>.
    ///
    /// Storage layout: <see cref="_entriesPerGroup"/> is a full-size array indexed
    /// directly by <see cref="GroupIndex.Index"/>. Slots for groups that don't
    /// match this set's template are <c>default(SetGroupEntry)</c> (<c>IsValid == false</c>).
    /// <see cref="_registeredGroups"/> is a compact list of the subset of groups
    /// this set covers — used for iteration paths (dep-tracking, flush, dispose).
    /// </summary>
    internal readonly unsafe struct EntitySetStorage
    {
        [NativeDisableContainerSafetyRestriction]
        internal readonly NativeList<SetGroupEntry> _entriesPerGroup;

        internal readonly NativeList<GroupIndex> _registeredGroups;

        internal readonly AtomicNativeBags _jobAddQueue;
        internal readonly AtomicNativeBags _jobRemoveQueue;

        // Set by NativeSetCommandBuffer.Clear() (race-write of 1 — idempotent across
        // threads), consumed and reset by SetFlushJob. Lives on the storage rather
        // than per-writer because writer jobs of the same set are serialized via
        // the scheduler — exactly one writer-job-cycle is in flight at a time, so
        // a single flag is sufficient.
        [NativeDisableUnsafePtrRestriction]
        internal readonly int* _jobClearRequested;

        readonly SetId _setId;

        internal EntitySetStorage(
            SetId setId,
            int totalGroupCount,
            DenseHashSet<GroupIndex> validGroups
        )
        {
            _setId = setId;

            _entriesPerGroup = new NativeList<SetGroupEntry>(totalGroupCount, Allocator.Persistent);
            _entriesPerGroup.Resize(totalGroupCount, NativeArrayOptions.ClearMemory);

            _registeredGroups = new NativeList<GroupIndex>(validGroups.Count, Allocator.Persistent);

            _jobAddQueue = AtomicNativeBags.Create(Allocator.Persistent);
            _jobRemoveQueue = AtomicNativeBags.Create(Allocator.Persistent);

            _jobClearRequested = (int*)UnsafeUtility.Malloc(sizeof(int), 4, Allocator.Persistent);
            *_jobClearRequested = 0;

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

        // ── Internal immediate operations (structural updates) ─────────
        //
        // Both assert that the group is non-null so callers can't silently
        // drop ops on a stale or default EntityIndex — null-group entries in
        // the deferred queues are caller bugs to surface.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddUnchecked(EntityIndex entityIndex)
        {
            Assert.That(!entityIndex.GroupIndex.IsNull);
            _entriesPerGroup[entityIndex.GroupIndex.Index].Add(entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveUnchecked(EntityIndex entityIndex)
        {
            Assert.That(!entityIndex.GroupIndex.IsNull);
            var entry = _entriesPerGroup[entityIndex.GroupIndex.Index];
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

        internal NativeSetCommandBuffer<TSet> CreateWriter<TSet>()
            where TSet : struct, IEntitySet =>
            new(_jobAddQueue, _jobRemoveQueue, _jobClearRequested);

        /// <summary>
        /// Drain all pending job writes into the actual group entries.
        /// Called on the main thread after outstanding writer jobs have completed.
        /// Removes are processed before adds so that re-add-after-remove works correctly.
        /// <para>
        /// Hidden invariant: by the time this runs, the writer's <see cref="SetFlushJob"/>
        /// has already executed (because the scheduler completes it during sync) and consumed
        /// the clear flag. So <c>*_jobClearRequested</c> is always 0 here. If a future code
        /// path ever fills the bags without going through a writer-job-cycle, this assertion
        /// will surface the missed flag-handling.
        /// </para>
        /// </summary>
        internal void FlushJobWrites()
        {
            Assert.That(
                *_jobClearRequested == 0,
                "FlushJobWrites called with a pending clear flag — a code path filled "
                    + "the job bags without going through a SetFlushJob, leaving the flag "
                    + "set. The clear must be handled (drain bags + clear entries) by the "
                    + "same path that set the flag."
            );
            for (int i = 0; i < _jobRemoveQueue.ThreadSlotCount; i++)
            {
                ref var bag = ref _jobRemoveQueue.GetBag(i);
                while (!bag.IsEmpty)
                {
                    var entityIndex = bag.Dequeue<EntityIndex>();
                    _entriesPerGroup[entityIndex.GroupIndex.Index].Remove(entityIndex.Index);
                }
            }

            for (int i = 0; i < _jobAddQueue.ThreadSlotCount; i++)
            {
                ref var bag = ref _jobAddQueue.GetBag(i);
                while (!bag.IsEmpty)
                {
                    var entityIndex = bag.Dequeue<EntityIndex>();
                    _entriesPerGroup[entityIndex.GroupIndex.Index].Add(entityIndex.Index);
                }
            }
        }

        // ── Bulk operations ────────────────────────────────────────────

        /// <summary>
        /// Clear all entries and drain pending job write queues. Used by
        /// the immediate-clear path (<see cref="WorldAccessor.ClearSet"/>),
        /// where a writer job may have completed but its enqueued entries
        /// haven't been flushed yet — those need to be discarded so the set
        /// stays empty after the call.
        /// <para>
        /// The deferred-clear path uses <see cref="ClearEntriesOnly"/>
        /// instead: by submission time, all writer jobs have completed and
        /// their <see cref="SetFlushJob"/>s have already drained the job
        /// queues, so re-draining is unnecessary.
        /// </para>
        /// <para>
        /// Neither variant touches <see cref="NativeSetDeferredQueues"/> —
        /// those are owned by <see cref="SetStore"/> and drained there.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            DrainEntityIndexBags(_jobAddQueue);
            DrainEntityIndexBags(_jobRemoveQueue);
            *_jobClearRequested = 0;
            ClearEntriesOnly();
        }

        /// <summary>
        /// Clear all entries without touching any of the queue structures.
        /// See <see cref="Clear"/> for when each variant applies.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearEntriesOnly()
        {
            for (int i = 0; i < _registeredGroups.Length; i++)
            {
                _entriesPerGroup[_registeredGroups[i].Index].Clear();
            }
        }

        /// <summary>
        /// Drain all entries from a bag-set, discarding the dequeued values.
        /// Shared by the immediate-clear path (job-write queues) and the
        /// deferred-clear path (deferred Add/Remove queues, when superseded
        /// by a queued <c>SetClear</c>).
        /// </summary>
        internal static void DrainEntityIndexBags(AtomicNativeBags bags)
        {
            for (int i = 0; i < bags.ThreadSlotCount; i++)
            {
                ref var bag = ref bags.GetBag(i);
                while (!bag.IsEmpty)
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
            UnsafeUtility.Free(_jobClearRequested, Allocator.Persistent);
        }
    }
}
