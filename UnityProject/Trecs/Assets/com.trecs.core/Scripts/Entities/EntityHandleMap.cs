using System;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Trecs
{
    // Bidirectional map between stable EntityHandles and transient EntityIndex values.
    // EntityHandle ids are reused via a free list; the Version field distinguishes
    // generations so stale handles are detected.
    internal struct EntityHandleMap : IDisposable
    {
        internal int _nextFreeIndex;

        [NativeDisableContainerSafetyRestriction]
        internal NativeList<EntityHandleMapElement> _entityHandleMap;

        bool _configurationFrozen;

        internal bool ConfigurationFrozen => _configurationFrozen;

        // Reverse map: per-group list of forward-map unique IDs (1-based),
        // indexed by GroupIndex.Index. Allocated once at world init with length
        // equal to the number of groups.
        // We intentionally store just the Id (4 bytes) instead of a full EntityHandle
        // (8 bytes). The Version is authoritative in the forward map, so any consumer that
        // needs a full handle (e.g. GetEntityHandle, NativeEntityHandleBuffer indexing)
        // fetches Version from there.
        // Invariant: each group's list length equals the group's live entity count.
        // TrimGroupList is called after structural changes (remove, move swap-back) to
        // maintain this. Every index in [0, groupList.Length) resolves to a live entity.
        // Inner is UnsafeList<int> (not NativeList) so that the overall type is a
        // NativeContainer holding a non-NativeContainer — legal inside jobs, unlike the
        // nested NativeArray<NativeList<int>> which Unity's safety system rejects.
        [NativeDisableContainerSafetyRestriction]
        internal NativeList<UnsafeList<int>> _entityIndexToReferenceMap;

        // Free-list note: unused slots in _entityHandleMap form a linked list
        // rooted at _nextFreeIndex, where each free element's Index field
        // points to the next free slot (and the chain terminates once
        // _nextFreeIndex reaches _entityHandleMap.Length, at which point we
        // bulk-allocate fresh sequential ids past the high-water mark). This
        // keeps free-list bookkeeping in the same memory the live entries
        // occupy.
        //
        // Both ClaimId and BatchClaimIds are main-thread-only. Job-side
        // entity adds must pre-reserve handles via ReserveEntityHandles on
        // the main thread and pass them to NativeWorldAccessor.AddEntity.
        /// <summary>
        /// Batch-claim multiple EntityHandles. Two-phase: drains the recycled
        /// free list first, then bulk-allocates fresh sequential IDs past the
        /// high-water mark. Much faster than calling ClaimId() N times
        /// (~30μs for 100K vs ~1ms).
        /// </summary>
        internal NativeArray<EntityHandle> BatchClaimIds(int count, Allocator allocator)
        {
            var refs = new NativeArray<EntityHandle>(count, allocator);
            int filled = 0;

            // Phase 1: drain free list (recycle slots)
            while (filled < count)
            {
                int tempFreeIndex = _nextFreeIndex;

                if (tempFreeIndex >= _entityHandleMap.Length)
                {
                    break;
                }

                ref var element = ref _entityHandleMap.ElementAt(tempFreeIndex);
                int newFreeIndex = element.Index;
                int version = element.Version;
                _nextFreeIndex = newFreeIndex;

#if TRECS_INTERNAL_CHECKS && DEBUG
                _entityHandleMap[tempFreeIndex] = new EntityHandleMapElement(
                    new EntityIndex(0, default),
                    version
                );
#endif

                refs[filled++] = new EntityHandle(tempFreeIndex + 1, version);
            }

            // Phase 2: bulk allocate fresh sequential IDs
            if (filled < count)
            {
                int remaining = count - filled;
                int baseIndex = _nextFreeIndex;
                _nextFreeIndex = baseIndex + remaining;
                for (int i = 0; i < remaining; i++)
                {
                    refs[filled + i] = new EntityHandle(baseIndex + i + 1, 0);
                }
            }

            return refs;
        }

        internal EntityHandle ClaimId()
        {
            int tempFreeIndex = _nextFreeIndex;
            int newFreeIndex;
            int version;
            if (tempFreeIndex >= _entityHandleMap.Length)
            {
                newFreeIndex = tempFreeIndex + 1;
                version = 0;
            }
            else
            {
                // Recycled slot — the free list links to the next-free slot
                // via the element's Index field.
                ref EntityHandleMapElement element = ref _entityHandleMap.ElementAt(tempFreeIndex);
                newFreeIndex = element.Index;
                version = element.Version;
            }
            _nextFreeIndex = newFreeIndex;

#if TRECS_INTERNAL_CHECKS && DEBUG
            if (tempFreeIndex < _entityHandleMap.Length)
            {
                _entityHandleMap[tempFreeIndex] = new EntityHandleMapElement(
                    new EntityIndex(0, default),
                    version
                );
            }
#endif

            return new EntityHandle(tempFreeIndex + 1, version);
        }

        internal void SetEntityHandle(EntityHandle reference, EntityIndex entityIndex)
        {
            // BatchClaimIds can hand out handle ids whose slot index sits past the
            // current map length (the bulk-allocate phase advances _nextFreeIndex
            // past _entityHandleMap.Length, deferring the actual resize to first
            // use). Resize here on demand to cover that case.

            if (reference.index >= _entityHandleMap.Length)
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                for (var i = _entityHandleMap.Length; i <= reference.index; i++)
                {
                    _entityHandleMap.Add(new EntityHandleMapElement(default, 0));
                }
#else
                _entityHandleMap.Resize(reference.index + 1, NativeArrayOptions.ClearMemory);
#endif
            }

#if TRECS_INTERNAL_CHECKS && DEBUG
            ref var entityHandleMapElement = ref _entityHandleMap.ElementAt(reference.index);
            if (
                entityHandleMapElement.Version != reference.Version
                || !entityHandleMapElement.EntityIndex.IsNull
            )
            {
                throw new TrecsException(
                    "Entity reference already set. This should never happen, please report it."
                );
            }
#endif
            _entityHandleMap[reference.index] = new EntityHandleMapElement(
                entityIndex,
                reference.Version
            );

            ref var groupList = ref GetGroupList(entityIndex.GroupIndex);
            EnsureGroupListSize(ref groupList, entityIndex.Index + 1);
            groupList[entityIndex.Index] = reference.Id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void UpdateEntityHandle(EntityIndex from, EntityIndex to)
        {
            ref var fromGroupList = ref _entityIndexToReferenceMap.ElementAt(from.GroupIndex.Index);
            var id = fromGroupList[from.Index];
            fromGroupList[from.Index] = 0;

            ref var element = ref _entityHandleMap.ElementAt(id - 1);
            element.Index = to.Index;
            element.GroupIndex = to.GroupIndex;

            ref var toGroupList = ref GetGroupList(to.GroupIndex);
            EnsureGroupListSize(ref toGroupList, to.Index + 1);
            toGroupList[to.Index] = id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void UpdateIndexAfterSwapBack(GroupIndex group, int oldIndex, int newIndex)
        {
            ref var groupList = ref _entityIndexToReferenceMap.ElementAt(group.Index);

            var id = groupList[oldIndex];

            // The entity at oldIndex may have been moved/removed already
            // (e.g., it was itself a moved entity whose reference was already handled).
            // In that case, skip the update.
            if (id == 0)
            {
                return;
            }

            groupList[oldIndex] = 0;
            EnsureGroupListSize(ref groupList, newIndex + 1);
            groupList[newIndex] = id;

            ref var element = ref _entityHandleMap.ElementAt(id - 1);
            element.Index = newIndex;
            element.GroupIndex = group;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveEntityHandle(EntityIndex entityIndex)
        {
            ref var groupList = ref _entityIndexToReferenceMap.ElementAt(
                entityIndex.GroupIndex.Index
            );
            var id = groupList[entityIndex.Index];
            TrecsDebugAssert.That(
                id != 0,
                "RemoveEntityHandle: null handle at index {0}",
                entityIndex
            );
            groupList[entityIndex.Index] = 0;

            // Invalidate by bumping version and linking the slot into the free list.
            ref var entityHandleMapElement = ref _entityHandleMap.ElementAt(id - 1);
            entityHandleMapElement.Index = _nextFreeIndex; // free-list link
            entityHandleMapElement.GroupIndex = default;
            entityHandleMapElement.BumpVersion();

            _nextFreeIndex = id - 1;
        }

        /// <summary>
        /// Batch update entity references for a set of entities moving between groups.
        /// Burst-compiled: the inner loop operates entirely on native data
        /// (UnsafeList, NativeList) so it runs through AOT codegen via .Run().
        /// </summary>
        internal void BatchUpdateEntityHandles(
            NativeList<MoveInfoEntry> entitiesToMove,
            GroupIndex fromGroup,
            GroupIndex toGroup
        )
        {
            ref var toGroupList = ref GetGroupList(toGroup);

            var count = entitiesToMove.Length;

            if (count > 0)
            {
                unsafe
                {
                    var entriesPtr = (MoveInfoEntry*)entitiesToMove.GetUnsafeReadOnlyPtr();
                    var maxToIndex = entriesPtr[0].Info.ToIndex;
                    for (int i = 1; i < count; i++)
                    {
                        if (entriesPtr[i].Info.ToIndex > maxToIndex)
                        {
                            maxToIndex = entriesPtr[i].Info.ToIndex;
                        }
                    }
                    EnsureGroupListSize(ref toGroupList, maxToIndex + 1);
                }
            }

            new BatchUpdateEntityHandlesJob
            {
                FromGroupList = _entityIndexToReferenceMap.ElementAt(fromGroup.Index),
                ToGroupList = toGroupList,
                EntityHandleMap = _entityHandleMap,
                EntriesToMove = entitiesToMove,
                ToGroup = toGroup,
            }.Run();
        }

        /// <summary>
        /// Step (a) of the Remove Refs apply phase. Schedules
        /// <see cref="Trecs.Internal.RemoveRefsCaptureJob"/> (Burst): builds the
        /// tail-slot lookup from the sorted-descending indices, then in
        /// submission order zeros each removed entity's per-group reverse-map
        /// slot and appends a <see cref="DeferredHandleFreeEntry"/> capture to
        /// <paramref name="deferredFreesOut"/>. The capture entries are later
        /// consumed by step (c) (BatchRelocateRemovedHandlesToTail) and then
        /// finalized after the OnRemoved fan-out.
        /// </summary>
        internal void BatchClearAndCaptureRemovedHandles(
            NativeList<int> entityHandlesToRemoveSubmissionOrder,
            NativeList<int> sortedDescendingIndices,
            int originalCount,
            GroupIndex fromGroup,
            NativeHashMap<int, int> tailSlotByOriginalSlot,
            NativeList<DeferredHandleFreeEntry> deferredFreesOut
        )
        {
            new RemoveRefsCaptureJob
            {
                GroupList = _entityIndexToReferenceMap.ElementAt(fromGroup.Index),
                RemoveIndicesSubmissionOrder = entityHandlesToRemoveSubmissionOrder,
                SortedDescendingIndices = sortedDescendingIndices,
                TailMap = tailSlotByOriginalSlot,
                DeferredFreesOut = deferredFreesOut,
                OriginalCount = originalCount,
                FromGroup = fromGroup,
            }.Run();
        }

        /// <summary>
        /// Step (c) of the Remove Refs apply phase. Schedules
        /// <see cref="Trecs.Internal.RemoveRefsRelocateJob"/> (Burst). Walks the
        /// per-group slice <c>[sliceStart, Length)</c> of
        /// <paramref name="deferredFrees"/> and writes each (id, tailSlot)
        /// capture into the reverse-map's tail position plus the forward
        /// handle map, so <c>EntityIndex.ToHandle</c> resolves removed entities
        /// during the OnRemoved fan-out. The handle's Version is intentionally
        /// NOT bumped here — handles obtained via ToHandle during OnRemoved must
        /// match the version visible before submission.
        /// </summary>
        internal void BatchRelocateRemovedHandlesToTail(
            GroupIndex fromGroup,
            NativeList<DeferredHandleFreeEntry> deferredFrees,
            int sliceStart
        )
        {
            new RemoveRefsRelocateJob
            {
                GroupList = _entityIndexToReferenceMap.ElementAt(fromGroup.Index),
                EntityHandleMap = _entityHandleMap,
                DeferredFrees = deferredFrees,
                SliceStart = sliceStart,
                FromGroup = fromGroup,
            }.Run();
        }

        /// <summary>
        /// Finalize a single deferred handle free: clears the tail slot,
        /// bumps the forward-map entry's version (invalidating any handle
        /// references the user kept across the callback), and returns the
        /// id to the free list. Called once per captured entry from
        /// EntitySubmitter.FinalizeDeferredHandleFrees after
        /// FireRemoveCallbacks has run.
        /// </summary>
        internal void FreeHandleAtTailSlot(GroupIndex group, int id, int tailSlot)
        {
            ref var groupList = ref _entityIndexToReferenceMap.ElementAt(group.Index);
            groupList[tailSlot] = 0;

            ref var element = ref _entityHandleMap.ElementAt(id - 1);
            element.Index = _nextFreeIndex;
            element.GroupIndex = default;
            element.BumpVersion();
            _nextFreeIndex = id - 1;
        }

        /// <summary>
        /// Batch update entity indices after swap-back for a group.
        /// Looks up the group list once instead of per entry.
        /// </summary>
        internal void BatchUpdateIndexAfterSwapBack(
            IterableDictionary<int, int> swapBackMapping,
            GroupIndex group
        )
        {
            ref var groupList = ref _entityIndexToReferenceMap.ElementAt(group.Index);

            foreach (var entry in swapBackMapping)
            {
                var id = groupList[entry.Key];

                if (id == 0)
                {
                    continue;
                }

                groupList[entry.Key] = 0;
                groupList[entry.Value] = id;

                ref var element = ref _entityHandleMap.ElementAt(id - 1);
                element.Index = entry.Value;
                element.GroupIndex = group;
            }
        }

#if TRECS_INTERNAL_CHECKS && DEBUG
        /// <summary>
        /// Verify that every non-null handle in a group's list has a matching
        /// _entityHandleMap entry pointing back to that group and position.
        /// </summary>
        internal void ValidateGroupConsistency(GroupIndex group, int entityCount)
        {
            ref var groupList = ref _entityIndexToReferenceMap.ElementAt(group.Index);
            if (!groupList.IsCreated)
                return;

            for (int i = 0; i < entityCount; i++)
            {
                var id = groupList[i];
                TrecsDebugAssert.That(
                    id != 0,
                    "ValidateGroupConsistency: null handle at index {0} in group {1} (entityCount={2})",
                    i,
                    group,
                    entityCount
                );

                ref var element = ref _entityHandleMap.ElementAt(id - 1);
                TrecsDebugAssert.That(
                    element.GroupIndex == group,
                    "ValidateGroupConsistency: group mismatch at index {0} in group {1}: element points to group {2}",
                    i,
                    group,
                    element.GroupIndex
                );
                TrecsDebugAssert.That(
                    element.Index == i,
                    "ValidateGroupConsistency: index mismatch at index {0} in group {1}: element points to index {2}",
                    i,
                    group,
                    element.Index
                );
            }
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveAllGroupReferenceLocators(GroupIndex groupId)
        {
            ref var groupList = ref _entityIndexToReferenceMap.ElementAt(groupId.Index);

            for (int i = 0; i < groupList.Length; i++)
            {
                var id = groupList[i];
                if (id == 0)
                {
                    continue;
                }

                ref var entityHandleMapElement = ref _entityHandleMap.ElementAt(id - 1);
                entityHandleMapElement.Index = _nextFreeIndex;
                entityHandleMapElement.GroupIndex = default;
                entityHandleMapElement.BumpVersion();

                _nextFreeIndex = id - 1;
            }

            groupList.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void UpdateAllGroupReferenceLocators(GroupIndex fromGroupId, GroupIndex toGroupId)
        {
            ref var fromGroupList = ref _entityIndexToReferenceMap.ElementAt(fromGroupId.Index);

            ref var toGroupList = ref GetGroupList(toGroupId);
            EnsureGroupListSize(ref toGroupList, fromGroupList.Length);

            for (int i = 0; i < fromGroupList.Length; i++)
            {
                var id = fromGroupList[i];
                if (id == 0)
                {
                    continue;
                }

                ref var element = ref _entityHandleMap.ElementAt(id - 1);
                element.Index = i;
                element.GroupIndex = toGroupId;

                toGroupList[i] = id;
            }

            fromGroupList.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly EntityHandle GetEntityHandle(EntityIndex entityIndex)
        {
            TrecsDebugAssert.That(!entityIndex.IsNull);

            var groupList = _entityIndexToReferenceMap[entityIndex.GroupIndex.Index];

            if (entityIndex.Index < groupList.Length)
            {
                var id = groupList[entityIndex.Index];
                if (id != 0)
                {
                    ref var element = ref _entityHandleMap.ElementAt(id - 1);
                    return new EntityHandle(id, element.Version);
                }
            }

            throw new TrecsException(
                $"Entity {entityIndex} does not exist. Common causes: "
                    + "just-created entity not yet submitted (use initializer.Handle "
                    + "from AddEntity), or stale EntityIndex from before a structural change."
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetEntityIndex(EntityHandle reference, out EntityIndex entityIndex)
        {
            entityIndex = default;

            if (reference == EntityHandle.Null)
            {
                return false;
            }

            if (reference.index >= _entityHandleMap.Length)
            {
                return false;
            }

            ref var entityHandleMapElement = ref _entityHandleMap.ElementAt(reference.index);
            if (entityHandleMapElement.Version == reference.Version)
            {
                entityIndex = entityHandleMapElement.EntityIndex;
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityIndex GetEntityIndex(EntityHandle reference)
        {
            TrecsDebugAssert.That(
                reference != EntityHandle.Null,
                "Attempting to get EntityIndex from null EntityHandle"
            );
            TrecsDebugAssert.That(
                reference.index < _entityHandleMap.Length,
                "Attempting to get EntityIndex from EntityHandle with index out of bounds"
            );
            ref var entityHandleMapElement = ref _entityHandleMap.ElementAt(reference.index);
            TrecsDebugAssert.That(
                entityHandleMapElement.Version == reference.Version,
                "Attempting to get EntityIndex from an EntityHandle that has been invalidated"
            );
            return entityHandleMapElement.EntityIndex;
        }

        internal void PreallocateIdMaps(GroupIndex groupId, int size)
        {
            // Lazy by default; the per-group list starts at capacity 0 from
            // InitEntityHandleMap and grows on first insert. This entry point
            // exists for callers (e.g. WorldAccessor.Warmup) that want to
            // pre-size buffers ahead of a burst of adds. Safe post-freeze.

            ref var groupList = ref GetGroupList(groupId);
            EnsureGroupListSize(ref groupList, size);

            if (size > _entityHandleMap.Capacity)
            {
                _entityHandleMap.Capacity = size;
            }
        }

        internal void InitEntityHandleMap(int groupCount)
        {
            _nextFreeIndex = 0;
            _entityHandleMap = new NativeList<EntityHandleMapElement>(0, Allocator.Persistent);
            _entityIndexToReferenceMap = new NativeList<UnsafeList<int>>(
                groupCount,
                Allocator.Persistent
            );
            _entityIndexToReferenceMap.Resize(groupCount, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < groupCount; i++)
            {
                _entityIndexToReferenceMap[i] = new UnsafeList<int>(0, Allocator.Persistent);
            }
        }

        internal void FreezeConfiguration()
        {
            _configurationFrozen = true;
        }

        public void Dispose()
        {
            _entityHandleMap.Dispose();

            for (int i = 0; i < _entityIndexToReferenceMap.Length; i++)
            {
                ref var list = ref _entityIndexToReferenceMap.ElementAt(i);
                if (list.IsCreated)
                {
                    list.Dispose();
                }
            }

            _entityIndexToReferenceMap.Dispose();
        }

        /// <summary>
        /// Shrinks a group's reverse-map list to <paramref name="newLength"/>. Called after
        /// structural changes (removals, moves) that reduce a group's live entity count so
        /// that the list length always matches the live count — callers can then rely on
        /// every index in [0, Length) being a live entity.
        /// <para>
        /// Safe to call unconditionally: trailing entries past the new count are guaranteed
        /// to be 0-sentinels by the swap-back protocol (removed slots are zeroed, and the
        /// tail entries from which entities were swapped back are zeroed too).
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void TrimGroupList(GroupIndex group, int newLength)
        {
            ref var groupList = ref _entityIndexToReferenceMap.ElementAt(group.Index);
            if (groupList.Length > newLength)
            {
                groupList.Resize(newLength, NativeArrayOptions.UninitializedMemory);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref UnsafeList<int> GetGroupList(GroupIndex group)
        {
            return ref _entityIndexToReferenceMap.ElementAt(group.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void EnsureGroupListSize(ref UnsafeList<int> groupList, int requiredSize)
        {
            if (groupList.Length < requiredSize)
            {
                groupList.Resize(requiredSize, NativeArrayOptions.ClearMemory);
            }
        }
    }

    /// <summary>
    /// Per-entity captured state for the deferred-free pipeline used during
    /// <see cref="Trecs.Internal.EntitySubmitter"/>'s OnRemoved fan-out. The
    /// removed entity's handle id is temporarily relocated to <c>TailSlot</c>
    /// so <c>EntityIndex.ToHandle</c> still resolves it during OnRemoved
    /// callbacks; after the callbacks return, the id is freed (version bump
    /// + push onto the free list) and the tail slot is zeroed. Blittable
    /// (<see cref="GroupIndex"/> is a single int) so a
    /// <c>NativeList&lt;DeferredHandleFreeEntry&gt;</c> can be populated
    /// directly from a Burst job.
    /// </summary>
    internal struct DeferredHandleFreeEntry
    {
        public GroupIndex Group;
        public int Id;
        public int TailSlot;
    }
}
