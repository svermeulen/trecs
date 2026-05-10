using System;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    // The EntityLocatorMap provides a bidirectional map to help locate entities without using an EntityIndex which might
    // change at runtime. The Entity Locator map uses a reusable unique identifier struct called EntityLocator to
    // find the last known EntityIndex from last entity submission.
    internal struct EntityHandleMap : IDisposable
    {
        internal SharedNativeInt _nextFreeIndex;

        [NativeDisableContainerSafetyRestriction]
        internal NativeList<EntityHandleMapElement> _entityHandleMap;

        bool _configurationFrozen;

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

        // (SVKJ) notes
        // This method is designed to work safely across threads
        // It does not cause _entityHandleMap to be re-allocated,
        // even though it does return indices into this array that exceed
        // the length
        // it is only when SetEntityHandle is called when this new index causes
        // a realloc (which, for jobs, is submission time)
        // Another note about this class is that it very cleverly embeds a linked list in the
        // unused slots of the native array, presumably to help with cache locality
        // and to minimize memory usage
        /// <summary>
        /// Batch-claim multiple EntityHandles on the main thread (single-threaded, no CAS needed).
        /// Two-phase: first drains the recycled free list, then bulk-allocates fresh sequential IDs.
        /// Much faster than calling ClaimId() N times (~30μs for 100K vs ~1ms).
        /// Must NOT be called concurrently with ClaimId or other batch claims.
        /// </summary>
        internal NativeArray<EntityHandle> BatchClaimIds(int count, Allocator allocator)
        {
            var refs = new NativeArray<EntityHandle>(count, allocator);
            int filled = 0;

            // Phase 1: drain free list (recycle slots, single-threaded, no CAS)
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
                _nextFreeIndex.Set(newFreeIndex);

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
                _nextFreeIndex.Set(baseIndex + remaining);
                for (int i = 0; i < remaining; i++)
                {
                    refs[filled + i] = new EntityHandle(baseIndex + i + 1, 0);
                }
            }

            return refs;
        }

        internal EntityHandle ClaimId()
        {
            int tempFreeIndex;
            int newFreeIndex;
            int version;

            do
            {
                tempFreeIndex = _nextFreeIndex;
                // Check if we need to create a new EntityLocator or whether we can recycle an existing one.
                if (tempFreeIndex >= _entityHandleMap.Length)
                {
                    newFreeIndex = tempFreeIndex + 1;
                    version = 0;
                }
                else
                {
                    ref EntityHandleMapElement element = ref _entityHandleMap.ElementAt(
                        tempFreeIndex
                    );
                    // The recycle entities form a linked list, using the entityIndex.Index to store the next element.
                    newFreeIndex = element.Index;
                    version = element.Version;
                }
            } while (tempFreeIndex != _nextFreeIndex.CompareExchange(newFreeIndex, tempFreeIndex));

#if TRECS_INTERNAL_CHECKS && DEBUG
            // This code should be safe since we own the tempFreeIndex, this allows us to later check that nothing went wrong.
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
            // Since references can be claimed in parallel now, it might happen that they are set out of order,
            // so we need to resize instead of add.

            if (reference.index >= _entityHandleMap.Length)
            {
#if TRECS_INTERNAL_CHECKS && DEBUG //THIS IS TO VALIDATE DATE DBC LIKE
                for (var i = _entityHandleMap.Length; i <= reference.index; i++)
                {
                    _entityHandleMap.Add(new EntityHandleMapElement(default, 0));
                }
#else
                _entityHandleMap.Resize(reference.index + 1, NativeArrayOptions.ClearMemory);
#endif
            }

#if TRECS_INTERNAL_CHECKS && DEBUG
            // These debug tests should be enough to detect if indices are being used correctly under native factories
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
            groupList[entityIndex.Index] = 0;

            // Invalidate the entity locator element by bumping its version and setting the entityIndex to point to a not existing element.
            ref var entityHandleMapElement = ref _entityHandleMap.ElementAt(id - 1);
            entityHandleMapElement.Index = _nextFreeIndex; //keep the free linked list updated
            entityHandleMapElement.GroupIndex = default;
            entityHandleMapElement.BumpVersion();

            // Mark the element as the last element used.
            _nextFreeIndex.Set(id - 1);
        }

        // OPTIMIZATION OPPORTUNITY: The three Batch* methods below are candidates for Burst compilation.
        // The inner loops are simple array-indexed reads/writes on native data (NativeList<int>,
        // NativeList<EntityHandleMapElement>). The main blockers are:
        // 1. Input collections (FastList, DenseDictionary) are managed — need marshaling to NativeArrays
        // 2. _nextFreeIndex (SharedNativeInt) uses Interlocked ops — need to expose raw int* for plain writes
        //    (safe because submission is single-threaded, though ClaimId needs atomics for job safety)
        // See docs/maintainer-docs/optimization_notes.md for detailed plan and proposed Burst job structs.

        /// <summary>
        /// Batch update entity references for a set of entities moving between groups.
        /// Looks up the from/to group lists once instead of per entity.
        /// </summary>
        internal void BatchUpdateEntityHandles(
            DenseDictionary<int, MoveInfo> entitiesToMove,
            GroupIndex fromGroup,
            GroupIndex toGroup
        )
        {
            ref var fromGroupList = ref _entityIndexToReferenceMap.ElementAt(fromGroup.Index);
            ref var toGroupList = ref GetGroupList(toGroup);

            var keys = entitiesToMove.UnsafeKeys;
            var values = entitiesToMove.UnsafeValues;
            var count = entitiesToMove.Count;

            // Ensure toGroupList is large enough for the largest destination index
            if (count > 0)
            {
                var maxToIndex = values[0].ToIndex;
                for (int i = 1; i < count; i++)
                {
                    if (values[i].ToIndex > maxToIndex)
                    {
                        maxToIndex = values[i].ToIndex;
                    }
                }
                EnsureGroupListSize(ref toGroupList, maxToIndex + 1);
            }

            for (int i = 0; i < count; i++)
            {
                var fromIndex = keys[i].key;
                var toIndex = values[i].ToIndex;

                var id = fromGroupList[fromIndex];
#if TRECS_INTERNAL_CHECKS && DEBUG
                Assert.That(
                    id != 0,
                    "BatchUpdateEntityHandles: null EntityHandle at fromIndex {} in group {}",
                    fromIndex,
                    fromGroup
                );
#endif
                fromGroupList[fromIndex] = 0;

                ref var element = ref _entityHandleMap.ElementAt(id - 1);
                element.Index = toIndex;
                element.GroupIndex = toGroup;

                toGroupList[toIndex] = id;
            }
        }

        /// <summary>
        /// Batch remove entity references for a set of entities being removed from a group.
        /// Looks up the group list once instead of per entity.
        /// </summary>
        internal void BatchRemoveEntityHandles(
            FastList<int> entityHandlesToRemove,
            GroupIndex fromGroup
        )
        {
            ref var groupList = ref _entityIndexToReferenceMap.ElementAt(fromGroup.Index);

            for (int i = 0; i < entityHandlesToRemove.Count; i++)
            {
                var entityArrayIndex = entityHandlesToRemove[i];

                var id = groupList[entityArrayIndex];
                Assert.That(
                    id != 0,
                    "BatchRemoveEntityHandles: null EntityHandle at index {} in group {}",
                    entityArrayIndex,
                    fromGroup
                );

                groupList[entityArrayIndex] = 0;

                ref var element = ref _entityHandleMap.ElementAt(id - 1);
                element.Index = _nextFreeIndex;
                element.GroupIndex = default;
                element.BumpVersion();
                _nextFreeIndex.Set(id - 1);
            }
        }

        /// <summary>
        /// Batch update entity indices after swap-back for a group.
        /// Looks up the group list once instead of per entry.
        /// </summary>
        internal void BatchUpdateIndexAfterSwapBack(
            DenseDictionary<int, int> swapBackMapping,
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
                Assert.That(
                    id != 0,
                    "ValidateGroupConsistency: null handle at index {} in group {} (entityCount={})",
                    i,
                    group,
                    entityCount
                );

                ref var element = ref _entityHandleMap.ElementAt(id - 1);
                Assert.That(
                    element.GroupIndex == group,
                    "ValidateGroupConsistency: group mismatch at index {} in group {}: element points to group {}",
                    i,
                    group,
                    element.GroupIndex
                );
                Assert.That(
                    element.Index == i,
                    "ValidateGroupConsistency: index mismatch at index {} in group {}: element points to index {}",
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

                _nextFreeIndex.Set(id - 1);
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
            Assert.That(!entityIndex.IsNull);

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
                $"Entity {entityIndex} does not exist. If you just created it, get it from initializer.Handle."
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

            // Make sure we are querying for the current version of the locator.
            // Otherwise the locator is pointing to a removed entity.
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
            Assert.That(
                reference != EntityHandle.Null,
                "Attempting to get EntityIndex from null EntityHandle"
            );
            Assert.That(
                reference.index < _entityHandleMap.Length,
                "Attempting to get EntityIndex from EntityHandle with index out of bounds"
            );
            // Make sure we are querying for the current version of the locator.
            // Otherwise the locator is pointing to a removed entity.
            ref var entityHandleMapElement = ref _entityHandleMap.ElementAt(reference.index);
            Assert.That(
                entityHandleMapElement.Version == reference.Version,
                "Attempting to get EntityIndex from an EntityHandle that has been invalidated"
            );
            return entityHandleMapElement.EntityIndex;
        }

        internal void PreallocateIdMaps(GroupIndex groupId, int size)
        {
            Assert.That(!_configurationFrozen);

            ref var groupList = ref GetGroupList(groupId);
            EnsureGroupListSize(ref groupList, size);

            if (size > _entityHandleMap.Capacity)
            {
                _entityHandleMap.Capacity = size;
            }
        }

        internal void InitEntityHandleMap(int groupCount)
        {
            _nextFreeIndex = SharedNativeInt.Create(0, Allocator.Persistent);
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
}
