using System;
using System.Collections.Generic;
using System.ComponentModel;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class SetStore : IDisposable
    {
        internal NativeHashMap<SetId, EntitySetStorage> EntitySets;
        internal NativeHashMap<SetId, NativeSetDeferredQueues> DeferredQueues;
        internal NativeList<SetId> SetIds;

        // Routing index: per-group list of set IDs registered for that group,
        // indexed by GroupIndex.Index. When an entity in group G is
        // removed/swapped, we look up all set IDs registered under G and
        // update them. Inner is UnsafeList<SetId> so the outer NativeList holds
        // non-NativeContainer values — same pattern as EntityHandleMap's reverse map.
        [NativeDisableContainerSafetyRestriction]
        internal NativeList<UnsafeList<SetId>> SetIdsByGroup;

        public SetStore(int groupCount)
        {
            EntitySets = new NativeHashMap<SetId, EntitySetStorage>(0, Allocator.Persistent);
            DeferredQueues = new NativeHashMap<SetId, NativeSetDeferredQueues>(
                0,
                Allocator.Persistent
            );
            SetIds = new NativeList<SetId>(0, Allocator.Persistent);
            SetIdsByGroup = new NativeList<UnsafeList<SetId>>(groupCount, Allocator.Persistent);
            SetIdsByGroup.Resize(groupCount, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < groupCount; i++)
            {
                SetIdsByGroup[i] = new UnsafeList<SetId>(0, Allocator.Persistent);
            }
        }

        /// <summary>
        /// Registers a set during world initialization. Creates the EntitySetStorage,
        /// pre-populates group entries, and populates the group-based routing index.
        /// </summary>
        public void RegisterSet(EntitySet entitySet, WorldInfo worldInfo)
        {
            TrecsDebugAssert.That(
                !EntitySets.ContainsKey(entitySet.Id),
                "Set '{0}' is already registered",
                entitySet.DebugName
            );

            var groups = entitySet.Tags.IsNull
                ? worldInfo.AllGroups
                : worldInfo.GetGroupsWithTags(entitySet.Tags);
            TrecsDebugAssert.That(
                groups.Count > 0,
                "Set '{0}' matched no groups. Are the tags used by a template added to the WorldBuilder?",
                entitySet.DebugName
            );

            var validGroups = new IterableHashSet<GroupIndex>(groups.Count);
            foreach (var group in groups)
            {
                validGroups.Add(group);
            }

            EntitySets.Add(
                entitySet.Id,
                new EntitySetStorage(entitySet.Id, worldInfo.AllGroups.Count, validGroups)
            );
            DeferredQueues.Add(
                entitySet.Id,
                new NativeSetDeferredQueues(
                    AtomicNativeBags.Create(Allocator.Persistent),
                    AtomicNativeBags.Create(Allocator.Persistent),
                    Allocator.Persistent
                )
            );
            SetIds.Add(entitySet.Id);

            foreach (var group in groups)
            {
                ref var list = ref SetIdsByGroup.ElementAt(group.Index);
                list.Add(entitySet.Id);
            }
        }

        internal EntitySetStorage GetSet(SetId setId)
        {
            var found = EntitySets.TryGetValue(setId, out var result);
            TrecsDebugAssert.That(
                found,
                "Set with ID '{0}' not registered. Add it to the WorldBuilder via AddSet<T>().",
                setId
            );
            return result;
        }

        internal EntitySetStorage GetSet(EntitySet entitySet)
        {
            return EntitySets[entitySet.Id];
        }

        internal NativeSetDeferredQueues GetDeferredQueues(SetId setId)
        {
            return DeferredQueues[setId];
        }

        /// <summary>
        /// Flush pending job writes on all sets.
        /// Called at phase boundaries after CompleteAllOutstanding().
        /// </summary>
        public void FlushAllSetJobWrites()
        {
            for (int i = 0; i < SetIds.Length; i++)
            {
                EntitySets[SetIds[i]].FlushJobWrites();
            }
        }

        /// <summary>
        /// Flush all pending deferred Add/Remove operations on all sets.
        /// Handles both main-thread and job-side deferred ops (unified into
        /// shared per-thread bags). Called during Submit.
        /// </summary>
        public void FlushAllDeferredOps()
        {
            for (int i = 0; i < SetIds.Length; i++)
            {
                var setId = SetIds[i];
                var set = EntitySets[setId];
                var queues = DeferredQueues[setId];
                FlushDeferredOpsForSet(ref set, ref queues);
            }
        }

        static void FlushDeferredOpsForSet(
            ref EntitySetStorage set,
            ref NativeSetDeferredQueues queues
        )
        {
            // Clear supersedes pending Add/Remove for this set, regardless of
            // call order — analogous to remove-supersedes-move on entity ops.
            // Job-side write queues (_jobAddQueue / _jobRemoveQueue inside
            // EntitySetStorage) are already empty by submission time — their
            // SetFlushJobs ran when the writer jobs completed — so the
            // deferred-clear path uses ClearEntriesOnly() rather than the
            // full Clear() that the immediate path needs.
            if (queues.ConsumeClearRequest())
            {
                EntitySetStorage.DrainEntityIndexBags(queues.AddQueue);
                EntitySetStorage.DrainEntityIndexBags(queues.RemoveQueue);
                set.ClearEntriesOnly();
                return;
            }

            var allRemoves = new NativeList<EntityIndex>(64, Allocator.Temp);
            for (int i = 0; i < queues.RemoveQueue.ThreadSlotCount; i++)
            {
                ref var bag = ref queues.RemoveQueue.GetBag(i);
                while (!bag.IsEmpty)
                    allRemoves.Add(bag.Dequeue<EntityIndex>());
            }
            allRemoves.Sort();
            for (int i = 0; i < allRemoves.Length; i++)
                set.RemoveUnchecked(allRemoves[i]);
            allRemoves.Dispose();

            var allAdds = new NativeList<EntityIndex>(64, Allocator.Temp);
            for (int i = 0; i < queues.AddQueue.ThreadSlotCount; i++)
            {
                ref var bag = ref queues.AddQueue.GetBag(i);
                while (!bag.IsEmpty)
                    allAdds.Add(bag.Dequeue<EntityIndex>());
            }
            allAdds.Sort();
            for (int i = 0; i < allAdds.Length; i++)
                set.AddUnchecked(allAdds[i]);
            allAdds.Dispose();
        }

        public void Dispose()
        {
            for (int i = 0; i < SetIds.Length; i++)
            {
                var setId = SetIds[i];
                EntitySets[setId].Dispose();
                DeferredQueues[setId].Dispose();
            }

            for (int i = 0; i < SetIdsByGroup.Length; i++)
            {
                ref var list = ref SetIdsByGroup.ElementAt(i);
                if (list.IsCreated)
                {
                    list.Dispose();
                }
            }

            EntitySets.Dispose();
            DeferredQueues.Dispose();
            SetIds.Dispose();
            SetIdsByGroup.Dispose();
        }

        /// <summary>
        /// Sets are automatically updated by the framework. If entities are removed
        /// from the database the sets are updated consequently.
        /// </summary>
        public void RemoveEntitiesFromSets(
            List<int> entityIndicesRemoved,
            GroupIndex fromGroup,
            IterableDictionary<int, int> entityIdsAffectedByRemoveAtSwapBack
        )
        {
            var setIds = SetIdsByGroup[fromGroup.Index];
            var numberOfSets = setIds.Length;
            if (numberOfSets == 0)
            {
                return;
            }

            for (int i = 0; i < numberOfSets; ++i)
            {
                var setCollection = EntitySets[setIds[i]];

                if (!setCollection.TryGetGroupEntry(fromGroup, out var fromGroupEntry))
                {
                    continue;
                }

                var entitiesCount = entityIndicesRemoved.Count;

                for (int entityIndex = 0; entityIndex < entitiesCount; ++entityIndex)
                {
                    int fromEntityIndex = entityIndicesRemoved[entityIndex];
                    fromGroupEntry.Remove(fromEntityIndex);
                }

                foreach (var entity in entityIdsAffectedByRemoveAtSwapBack)
                {
                    var resultEntityHandleToDenseIndex = fromGroupEntry._entityIdToDenseIndex;
                    if (resultEntityHandleToDenseIndex.Remove(entity.Key))
                    {
                        resultEntityHandleToDenseIndex.TryAdd(entity.Value, entity.Value, out _);
                    }
                }
            }
        }

        public void SwapEntityBetweenSets(
            NativeList<MoveInfoEntry> fromEntityToEntityIDs,
            GroupIndex fromGroup,
            GroupIndex toGroup,
            IterableDictionary<int, int> entityIdsAffectedByRemoveAtSwapBack
        )
        {
            var setIds = SetIdsByGroup[fromGroup.Index];
            var numberOfSets = setIds.Length;
            if (numberOfSets == 0)
            {
                return;
            }

            for (int i = 0; i < numberOfSets; ++i)
            {
                var setCollection = EntitySets[setIds[i]];

                if (!setCollection.TryGetGroupEntry(fromGroup, out var fromGroupEntry))
                {
                    continue;
                }

                SetGroupEntry groupEntryTo = default;
                bool resolvedToGroup = false;

                var countOfEntitiesToSwap = fromEntityToEntityIDs.Length;

                unsafe
                {
                    var entriesPtr = (MoveInfoEntry*)fromEntityToEntityIDs.GetUnsafeReadOnlyPtr();
                    for (var index = 0; index < countOfEntitiesToSwap; index++)
                    {
                        var swapEntry = entriesPtr[index];
                        int fromEntityIndex = swapEntry.EntityIndex;
                        int toIndex = swapEntry.Info.ToIndex;

                        if (fromGroupEntry.Remove(fromEntityIndex))
                        {
                            if (!resolvedToGroup)
                            {
                                resolvedToGroup = true;
                                setCollection.TryGetGroupEntry(toGroup, out groupEntryTo);
                            }

                            if (groupEntryTo.IsValid)
                            {
                                groupEntryTo.Add(toIndex);
                            }
                        }
                    }
                }

                foreach (var entity in entityIdsAffectedByRemoveAtSwapBack)
                {
                    var resultEntityHandleToDenseIndex = fromGroupEntry._entityIdToDenseIndex;
                    if (resultEntityHandleToDenseIndex.Remove(entity.Key))
                    {
                        resultEntityHandleToDenseIndex.TryAdd(entity.Value, entity.Value, out _);
                    }
                }
            }
        }

        public bool HasAnySets => SetIds.Length > 0;

        public EntityQuerier.TrecsSets GetTrecsSets()
        {
            return new EntityQuerier.TrecsSets(EntitySets);
        }
    }
}
