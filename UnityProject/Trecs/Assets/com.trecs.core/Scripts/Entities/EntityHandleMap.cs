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
    //
    // This is a managed class — the single canonical instance is owned by
    // EntityQuerier and mutated only on the main thread. Burst jobs that need
    // handle resolution embed the read-only <see cref="EntityHandleMapView"/>
    // (obtained via <see cref="View"/>) instead, which aliases the same native
    // containers but exposes none of the mutating API.
    internal sealed unsafe class EntityHandleMap : IDisposable
    {
        // Free-list head. Lives in native memory (rather than a plain int field)
        // so that FullGroupFreeHandlesJob can write it through a stable pointer
        // (see BatchFreeWholeGroupHandles).
        NativeReference<int> _nextFreeIndex;

        // Cached raw pointer into _nextFreeIndex's allocation, valid for the
        // map's whole lifetime (the NativeReference is allocated once in the
        // constructor and disposed in Dispose). ClaimId and FreeHandleAtTailSlot
        // run per entity per frame; going through NativeReference.Value there
        // would pay a collections safety-check on every access in the editor
        // (~30% on the bench's add/remove spans), so all internal accesses
        // dereference this instead.
        readonly int* _nextFreeIndexPtr;

        NativeList<EntityHandleMapElement> _entityHandleMap;

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
        NativeList<UnsafeList<int>> _entityIndexToReferenceMap;

        readonly EntityHandleMapView _view;

        internal EntityHandleMapView View => _view;

        internal EntityHandleMap(int groupCount)
        {
            _nextFreeIndex = new NativeReference<int>(0, Allocator.Persistent);
            _nextFreeIndexPtr = _nextFreeIndex.GetUnsafePtr();
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

            // Safe to capture once: the NativeList handles are never reassigned
            // after construction — resizes (including deserialization, which
            // reuses the already-created lists) mutate the underlying buffers
            // through these same handles.
            _view = new EntityHandleMapView(_entityHandleMap, _entityIndexToReferenceMap);
        }

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
                int tempFreeIndex = *_nextFreeIndexPtr;

                if (tempFreeIndex >= _entityHandleMap.Length)
                {
                    break;
                }

                ref var element = ref _entityHandleMap.ElementAt(tempFreeIndex);
                int newFreeIndex = element.Index;
                int version = element.Version;
                *_nextFreeIndexPtr = newFreeIndex;

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
                int baseIndex = *_nextFreeIndexPtr;
                *_nextFreeIndexPtr = baseIndex + remaining;
                for (int i = 0; i < remaining; i++)
                {
                    refs[filled + i] = new EntityHandle(baseIndex + i + 1, 0);
                }
            }

            return refs;
        }

        internal EntityHandle ClaimId()
        {
            int tempFreeIndex = *_nextFreeIndexPtr;
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
            *_nextFreeIndexPtr = newFreeIndex;

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

        /// <summary>
        /// Batch update entity references for a set of entities moving between groups.
        /// The per-entity update loop runs in a Burst job via .Run(); only the
        /// list-sizing prep here stays managed.
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
            element.Index = *_nextFreeIndexPtr;
            element.GroupIndex = default;
            element.BumpVersion();
            *_nextFreeIndexPtr = id - 1;
        }

        /// <summary>
        /// Frees every handle of a whole-group removal in one Burst pass. The group's
        /// reverse-map slots <c>[0, count)</c> still hold their (untouched) ids — a
        /// whole-group removal has no swap-back, so unlike the per-entity path there's
        /// nothing to capture or relocate. This walks them in ascending slot order,
        /// bumping each forward-map version and linking the freed id into the free
        /// list. Sequential ordering matches the per-entity path's free-list push
        /// order, so the determinism checksum is unchanged. Caller trims the group
        /// list to 0 afterward.
        /// </summary>
        internal void BatchFreeWholeGroupHandles(GroupIndex group, int count)
        {
            if (count == 0)
            {
                return;
            }

            unsafe
            {
                // NextFreeIndexPtr points at the NativeReference's native
                // allocation — stable memory, safe for the job to write through.
                // Run with .Run() (not .Schedule()) because callers read the
                // free-list head immediately afterward.
                // count never exceeds GroupList.Length: the reverse-map list always
                // covers at least the live entity count (Preallocate may make it
                // longer, never shorter), so the [0, count) walk stays in bounds.
                new FullGroupFreeHandlesJob
                {
                    GroupList = _entityIndexToReferenceMap.ElementAt(group.Index),
                    EntityHandleMap = _entityHandleMap,
                    NextFreeIndexPtr = (long)_nextFreeIndexPtr,
                    Count = count,
                }.Run();
            }
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
        public EntityHandle GetEntityHandle(EntityIndex entityIndex)
        {
            return _view.GetEntityHandle(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetEntityIndex(EntityHandle reference, out EntityIndex entityIndex)
        {
            return _view.TryGetEntityIndex(reference, out entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityIndex GetEntityIndex(EntityHandle reference)
        {
            return _view.GetEntityIndex(reference);
        }

        /// <summary>
        /// Builds the job-visible handle buffer for a group: the group's
        /// reverse-map slice plus the forward map. Returns default when the
        /// group's reverse map hasn't been created yet.
        /// <para>
        /// The buffer captures raw pointers and lengths at call time, so it is
        /// only valid until the next structural change (which can resize either
        /// list) — i.e. for the job batch being scheduled now, matching
        /// <see cref="NativeBuffer{T}"/>'s documented lifetime contract.
        /// </para>
        /// </summary>
        internal NativeEntityHandleBuffer GetHandleBufferForJobScheduling(GroupIndex group)
        {
            var groupList = _entityIndexToReferenceMap[group.Index];
            if (!groupList.IsCreated)
                return default;
            return new NativeEntityHandleBuffer(
                new NativeBuffer<int>(groupList.Ptr, groupList.Length),
                NativeBuffer<EntityHandleMapElement>.FromNativeList(_entityHandleMap)
            );
        }

        internal void PreallocateIdMaps(GroupIndex groupId, int size)
        {
            // Lazy by default; the per-group list starts at capacity 0 from
            // the constructor and grows on first insert. This entry point
            // exists for callers (e.g. WorldAccessor.Warmup) that want to
            // pre-size buffers ahead of a burst of adds. Safe post-freeze.

            ref var groupList = ref GetGroupList(groupId);
            EnsureGroupListSize(ref groupList, size);

            if (size > _entityHandleMap.Capacity)
            {
                _entityHandleMap.Capacity = size;
            }
        }

        /// <summary>
        /// Writes the full handle-map state (forward map, per-group reverse
        /// maps, free-list head) to the snapshot stream. The wire format lives
        /// here, next to the storage it mirrors; <see cref="WorldStateSerializer"/>
        /// only owns section ordering.
        /// </summary>
        internal void Serialize(ISerializationWriter writer, WorldInfo worldInfo)
        {
            writer.Write("EntityIdMap", in _entityHandleMap);

            writer.PushScope("ReferenceMap");
            WriteEntityIndexToReferenceMap(writer, worldInfo);
            writer.PopScope();

            int nextFreeIndex = _nextFreeIndex.Value;
            writer.Write("NextFreeIndex", nextFreeIndex);
        }

        internal void Deserialize(ISerializationReader reader, WorldInfo worldInfo)
        {
            var listHandleBefore = _entityHandleMap.GetUnsafeList();
            reader.Read("EntityIdMap", ref _entityHandleMap);
            // The serializer must fill the existing list in place (it does, as
            // long as the list is created — which the constructor guarantees).
            // If it ever swapped in a fresh NativeList, _view and every
            // NativeWorldAccessor's captured EntityHandleMapView would silently
            // dangle, so fail loudly here instead.
            TrecsDebugAssert.That(
                listHandleBefore == _entityHandleMap.GetUnsafeList(),
                "EntityHandleMap.Deserialize reassigned the forward-map NativeList handle; "
                    + "all captured views are now stale. The deserializer must fill in place."
            );
            ReadEntityIndexToReferenceMap(reader, worldInfo);

            var nextFreeIndex = reader.Read<int>("NextFreeIndex");
            TrecsDebugAssert.That(nextFreeIndex >= 0);

            _nextFreeIndex.Value = nextFreeIndex;
        }

        // Reverse-map groups are keyed by TagSet on the wire (not raw
        // GroupIndex) so snapshots stay valid across group-index reassignment
        // between world builds.
        void WriteEntityIndexToReferenceMap(ISerializationWriter writer, WorldInfo worldInfo)
        {
            var count = _entityIndexToReferenceMap.Length;

            writer.Write("Count", count);

            for (int i = 0; i < count; i++)
            {
                writer.PushScope("Ref{0}", i);
                var tagSet = worldInfo.ToTagSet(GroupIndex.FromIndex(i));
                writer.Write("Group", tagSet);

                writer.Write("Refs", _entityIndexToReferenceMap[i]);
                writer.PopScope();
            }
        }

        void ReadEntityIndexToReferenceMap(ISerializationReader reader, WorldInfo worldInfo)
        {
            var count = reader.Read<int>("Count");
            TrecsDebugAssert.That(count >= 0);

            TrecsDebugAssert.IsEqual(count, _entityIndexToReferenceMap.Length);

            for (int i = 0; i < count; i++)
            {
                var tagSet = reader.Read<TagSet>("Group");
                var group = worldInfo.ToGroupIndex(tagSet);

                ref var groupList = ref _entityIndexToReferenceMap.ElementAt(group.Index);
                reader.Read("Refs", ref groupList);
            }
        }

        public void Dispose()
        {
            _nextFreeIndex.Dispose();
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
    /// Read-only, Burst-compatible view over <see cref="EntityHandleMap"/>'s native
    /// containers, for embedding in job-visible structs (e.g.
    /// <see cref="NativeWorldAccessor"/>). Exposes only handle resolution — claiming
    /// and freeing ids stays on the managed class, so job code structurally cannot
    /// mutate free-list state through a copied view.
    /// </summary>
    internal readonly struct EntityHandleMapView
    {
        [NativeDisableContainerSafetyRestriction]
        readonly NativeList<EntityHandleMapElement> _entityHandleMap;

        // See the field of the same name on EntityHandleMap for the full
        // reverse-map design notes.
        [NativeDisableContainerSafetyRestriction]
        readonly NativeList<UnsafeList<int>> _entityIndexToReferenceMap;

        internal EntityHandleMapView(
            NativeList<EntityHandleMapElement> entityHandleMap,
            NativeList<UnsafeList<int>> entityIndexToReferenceMap
        )
        {
            _entityHandleMap = entityHandleMap;
            _entityIndexToReferenceMap = entityIndexToReferenceMap;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityHandle GetEntityHandle(EntityIndex entityIndex)
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
