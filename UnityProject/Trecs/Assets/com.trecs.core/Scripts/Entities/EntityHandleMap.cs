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

        // Reverse map: per-group NativeList of forward-map unique IDs (1-based; 0 = empty slot).
        // We intentionally store just the UniqueId (4 bytes) instead of a full EntityHandle
        // (8 bytes). The Version is authoritative in the forward map, so any consumer that
        // needs a full handle (e.g. GetEntityHandle, NativeEntityHandleBuffer indexing)
        // fetches Version from there.
        internal NativeDenseDictionary<Group, NativeList<int>> _entityIndexToReferenceMap;

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
                int newFreeIndex = element.EntityIndex.Index;
                int version = element.Version;
                _nextFreeIndex.Set(newFreeIndex);

#if TRECS_INTERNAL_CHECKS && DEBUG
                _entityHandleMap[tempFreeIndex] = new EntityHandleMapElement(
                    new EntityIndex(0, new Group(0)),
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
                    newFreeIndex = element.EntityIndex.Index;
                    version = element.Version;
                }
            } while (tempFreeIndex != _nextFreeIndex.CompareExchange(newFreeIndex, tempFreeIndex));

#if TRECS_INTERNAL_CHECKS && DEBUG
            // This code should be safe since we own the tempFreeIndex, this allows us to later check that nothing went wrong.
            if (tempFreeIndex < _entityHandleMap.Length)
            {
                _entityHandleMap[tempFreeIndex] = new EntityHandleMapElement(
                    new EntityIndex(0, new Group(0)),
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
                || entityHandleMapElement.EntityIndex.Group != Group.Null
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

            var groupList = GetOrCreateGroupList(entityIndex.Group);
            EnsureGroupListSize(groupList, entityIndex.Index + 1);
            groupList[entityIndex.Index] = reference.UniqueId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void UpdateEntityHandle(EntityIndex from, EntityIndex to)
        {
            var fromGroupList = _entityIndexToReferenceMap[from.Group];
            var uniqueId = fromGroupList[from.Index];
            fromGroupList[from.Index] = 0;

            _entityHandleMap.ElementAt(uniqueId - 1).EntityIndex = to;

            var toGroupList = GetOrCreateGroupList(to.Group);
            EnsureGroupListSize(toGroupList, to.Index + 1);
            toGroupList[to.Index] = uniqueId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void UpdateIndexAfterSwapBack(Group group, int oldIndex, int newIndex)
        {
            var groupList = _entityIndexToReferenceMap[group];

            var uniqueId = groupList[oldIndex];

            // The entity at oldIndex may have been moved/removed already
            // (e.g., it was itself a moved entity whose reference was already handled).
            // In that case, skip the update.
            if (uniqueId == 0)
            {
                return;
            }

            groupList[oldIndex] = 0;
            EnsureGroupListSize(groupList, newIndex + 1);
            groupList[newIndex] = uniqueId;

            _entityHandleMap.ElementAt(uniqueId - 1).EntityIndex = new EntityIndex(newIndex, group);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveEntityHandle(EntityIndex entityIndex)
        {
            var groupList = _entityIndexToReferenceMap[entityIndex.Group];
            var uniqueId = groupList[entityIndex.Index];
            groupList[entityIndex.Index] = 0;

            // Invalidate the entity locator element by bumping its version and setting the entityIndex to point to a not existing element.
            ref var entityHandleMapElement = ref _entityHandleMap.ElementAt(uniqueId - 1);
            entityHandleMapElement.EntityIndex = new EntityIndex(_nextFreeIndex, new Group(0)); //keep the free linked list updated
            entityHandleMapElement.Version++;

            // Mark the element as the last element used.
            _nextFreeIndex.Set(uniqueId - 1);
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
            Group fromGroup,
            Group toGroup
        )
        {
            var fromGroupList = _entityIndexToReferenceMap[fromGroup];
            var toGroupList = GetOrCreateGroupList(toGroup);

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
                EnsureGroupListSize(toGroupList, maxToIndex + 1);
            }

            for (int i = 0; i < count; i++)
            {
                var fromIndex = keys[i].key;
                var toIndex = values[i].ToIndex;

                var uniqueId = fromGroupList[fromIndex];
#if TRECS_INTERNAL_CHECKS && DEBUG
                Assert.That(
                    uniqueId != 0,
                    "BatchUpdateEntityHandles: null EntityHandle at fromIndex {} in group {}",
                    fromIndex,
                    fromGroup
                );
#endif
                fromGroupList[fromIndex] = 0;

                _entityHandleMap.ElementAt(uniqueId - 1).EntityIndex = new EntityIndex(
                    toIndex,
                    toGroup
                );

                toGroupList[toIndex] = uniqueId;
            }
        }

        /// <summary>
        /// Batch remove entity references for a set of entities being removed from a group.
        /// Looks up the group list once instead of per entity.
        /// </summary>
        internal void BatchRemoveEntityHandles(FastList<int> entityHandlesToRemove, Group fromGroup)
        {
            var groupList = _entityIndexToReferenceMap[fromGroup];

            for (int i = 0; i < entityHandlesToRemove.Count; i++)
            {
                var entityArrayIndex = entityHandlesToRemove[i];

                var uniqueId = groupList[entityArrayIndex];
                Assert.That(
                    uniqueId != 0,
                    "BatchRemoveEntityHandles: null EntityHandle at index {} in group {}",
                    entityArrayIndex,
                    fromGroup
                );

                groupList[entityArrayIndex] = 0;

                ref var element = ref _entityHandleMap.ElementAt(uniqueId - 1);
                element.EntityIndex = new EntityIndex(_nextFreeIndex, new Group(0));
                element.Version++;
                _nextFreeIndex.Set(uniqueId - 1);
            }
        }

        /// <summary>
        /// Batch update entity indices after swap-back for a group.
        /// Looks up the group list once instead of per entry.
        /// </summary>
        internal void BatchUpdateIndexAfterSwapBack(
            DenseDictionary<int, int> swapBackMapping,
            Group group
        )
        {
            var groupList = _entityIndexToReferenceMap[group];

            foreach (var entry in swapBackMapping)
            {
                var uniqueId = groupList[entry.Key];

                if (uniqueId == 0)
                {
                    continue;
                }

                groupList[entry.Key] = 0;
                groupList[entry.Value] = uniqueId;

                _entityHandleMap.ElementAt(uniqueId - 1).EntityIndex = new EntityIndex(
                    entry.Value,
                    group
                );
            }
        }

#if TRECS_INTERNAL_CHECKS && DEBUG
        /// <summary>
        /// Verify that every non-null handle in a group's list has a matching
        /// _entityHandleMap entry pointing back to that group and position.
        /// </summary>
        internal void ValidateGroupConsistency(Group group, int entityCount)
        {
            if (!_entityIndexToReferenceMap.TryGetValue(group, out var groupList))
                return;

            for (int i = 0; i < entityCount; i++)
            {
                var uniqueId = groupList[i];
                Assert.That(
                    uniqueId != 0,
                    "ValidateGroupConsistency: null handle at index {} in group {} (entityCount={})",
                    i,
                    group,
                    entityCount
                );

                ref var element = ref _entityHandleMap.ElementAt(uniqueId - 1);
                Assert.That(
                    element.EntityIndex.Group == group,
                    "ValidateGroupConsistency: group mismatch at index {} in group {}: element points to group {}",
                    i,
                    group,
                    element.EntityIndex.Group
                );
                Assert.That(
                    element.EntityIndex.Index == i,
                    "ValidateGroupConsistency: index mismatch at index {} in group {}: element points to index {}",
                    i,
                    group,
                    element.EntityIndex.Index
                );
            }
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveAllGroupReferenceLocators(Group groupId)
        {
            if (!_entityIndexToReferenceMap.TryGetValue(groupId, out var groupList))
            {
                Assert.That(
                    !_configurationFrozen,
                    "Referenced unrecognized group after configuration has been frozen"
                );
                return;
            }

            for (int i = 0; i < groupList.Length; i++)
            {
                var uniqueId = groupList[i];
                if (uniqueId == 0)
                {
                    continue;
                }

                ref var entityHandleMapElement = ref _entityHandleMap.ElementAt(uniqueId - 1);
                entityHandleMapElement.EntityIndex = new EntityIndex(_nextFreeIndex, new Group(0));
                entityHandleMapElement.Version++;

                _nextFreeIndex.Set(uniqueId - 1);
            }

            groupList.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void UpdateAllGroupReferenceLocators(Group fromGroupId, Group toGroupId)
        {
            if (!_entityIndexToReferenceMap.TryGetValue(fromGroupId, out var fromGroupList))
            {
                Assert.That(
                    !_configurationFrozen,
                    "Referenced unrecognized group after configuration has been frozen"
                );
                return;
            }

            var toGroupList = GetOrCreateGroupList(toGroupId);
            EnsureGroupListSize(toGroupList, fromGroupList.Length);

            for (int i = 0; i < fromGroupList.Length; i++)
            {
                var uniqueId = fromGroupList[i];
                if (uniqueId == 0)
                {
                    continue;
                }

                _entityHandleMap.ElementAt(uniqueId - 1).EntityIndex = new EntityIndex(
                    i,
                    toGroupId
                );

                toGroupList[i] = uniqueId;
            }

            fromGroupList.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly EntityHandle GetEntityHandle(EntityIndex entityIndex)
        {
            Assert.That(!entityIndex.IsNull);

            if (_entityIndexToReferenceMap.TryGetValue(entityIndex.Group, out var groupList))
            {
                if (entityIndex.Index < groupList.Length)
                {
                    var uniqueId = groupList[entityIndex.Index];
                    if (uniqueId != 0)
                    {
                        ref var element = ref _entityHandleMap.ElementAt(uniqueId - 1);
                        return new EntityHandle(uniqueId, element.Version);
                    }
                }

                throw new TrecsException(
                    $"Entity {entityIndex} does not exist. If you just created it, get it from initializer.Handle."
                );
            }

            Assert.That(
                !_configurationFrozen,
                "Referenced unrecognized group after configuration has been frozen"
            );

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

        internal void PreallocateIdMaps(Group groupId, int size)
        {
            Assert.That(!_configurationFrozen);

            var groupList = GetOrCreateGroupList(groupId);
            EnsureGroupListSize(groupList, size);

            if (size > _entityHandleMap.Capacity)
            {
                _entityHandleMap.Capacity = size;
            }
        }

        internal void InitEntityHandleMap()
        {
            _nextFreeIndex = SharedNativeInt.Create(0);
            _entityHandleMap = new NativeList<EntityHandleMapElement>(0, Allocator.Persistent);
            _entityIndexToReferenceMap = new NativeDenseDictionary<Group, NativeList<int>>(
                0,
                Allocator.Persistent
            );
        }

        internal void FreezeConfiguration()
        {
            _configurationFrozen = true;
        }

        public void Dispose()
        {
            _nextFreeIndex.Dispose();
            _entityHandleMap.Dispose();

            foreach (var element in _entityIndexToReferenceMap)
            {
                element.Value.Dispose();
            }

            _entityIndexToReferenceMap.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        NativeList<int> GetOrCreateGroupList(Group group)
        {
            if (!_entityIndexToReferenceMap.TryGetValue(group, out var groupList))
            {
                Assert.That(
                    !_configurationFrozen,
                    "Referenced unrecognized group after configuration has been frozen"
                );
                groupList = new NativeList<int>(0, Allocator.Persistent);
                _entityIndexToReferenceMap.Add(group, groupList);
            }

            return groupList;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void EnsureGroupListSize(NativeList<int> groupList, int requiredSize)
        {
            if (groupList.Length < requiredSize)
            {
                groupList.Resize(requiredSize, NativeArrayOptions.ClearMemory);
            }
        }
    }
}
