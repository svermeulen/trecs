using System;
using System.ComponentModel;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class SetStore : IDisposable
    {
        internal NativeDenseDictionary<SetId, EntitySetStorage> EntitySets;
        internal NativeDenseDictionary<SetId, NativeSetDeferredQueues> DeferredQueues;

        // Routing index: per-group list of set IDs registered for that group,
        // indexed by GroupIndex.Index. When an entity in group G is
        // removed/swapped, we look up all set IDs registered under G and
        // update them. Inner is UnsafeList<SetId> so the outer NativeList holds
        // non-NativeContainer values — same pattern as EntityHandleMap's reverse map.
        [NativeDisableContainerSafetyRestriction]
        internal NativeList<UnsafeList<SetId>> SetIdsByGroup;

        public SetStore(int groupCount)
        {
            EntitySets = new NativeDenseDictionary<SetId, EntitySetStorage>(
                0,
                Allocator.Persistent
            );
            DeferredQueues = new NativeDenseDictionary<SetId, NativeSetDeferredQueues>(
                0,
                Allocator.Persistent
            );
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
            TrecsAssert.That(
                !EntitySets.ContainsKey(entitySet.Id),
                "Set '{0}' is already registered",
                entitySet.DebugName
            );

            var groups = entitySet.Tags.IsNull
                ? worldInfo.AllGroups
                : worldInfo.GetGroupsWithTags(entitySet.Tags);
            TrecsAssert.That(
                groups.Count > 0,
                "Set '{0}' matched no groups. Are the tags used by a template added to the WorldBuilder?",
                entitySet.DebugName
            );

            var validGroups = new DenseHashSet<GroupIndex>(groups.Count);
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

            foreach (var group in groups)
            {
                ref var list = ref SetIdsByGroup.ElementAt(group.Index);
                list.Add(entitySet.Id);
            }
        }

        internal ref EntitySetStorage GetSet(SetId setId)
        {
            var success = EntitySets.TryGetIndex(setId, out var index);
            TrecsAssert.That(
                success,
                "Set with ID '{0}' not registered. Add it to the WorldBuilder via AddSet<T>().",
                setId
            );
            return ref EntitySets.GetValueAtIndexByRef(index);
        }

        internal ref EntitySetStorage GetSet(EntitySet entitySet)
        {
            return ref EntitySets.GetValueByRef(entitySet.Id);
        }

        internal ref NativeSetDeferredQueues GetDeferredQueues(SetId setId)
        {
            return ref DeferredQueues.GetValueByRef(setId);
        }

        /// <summary>
        /// Flush pending job writes on all sets.
        /// Called at phase boundaries after CompleteAllOutstanding().
        /// </summary>
        public void FlushAllSetJobWrites()
        {
            var sets = EntitySets.UnsafeValues;
            var count = EntitySets.Count;
            for (int i = 0; i < count; i++)
            {
                sets[i].FlushJobWrites();
            }
        }

        /// <summary>
        /// Flush all pending deferred Add/Remove operations on all sets.
        /// Handles both main-thread and job-side deferred ops (unified into
        /// shared per-thread bags). Called during SubmitEntities.
        /// </summary>
        public void FlushAllDeferredOps(bool requireDeterministic)
        {
            foreach (var entry in DeferredQueues)
            {
                ref var set = ref EntitySets.GetValueByRef(entry.Key);
                ref var queues = ref DeferredQueues.GetValueByRef(entry.Key);
                FlushDeferredOpsForSet(ref set, ref queues, requireDeterministic);
            }
        }

        static void FlushDeferredOpsForSet(
            ref EntitySetStorage set,
            ref NativeSetDeferredQueues queues,
            bool requireDeterministic
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

            if (requireDeterministic)
                FlushDeferredOpsDeterministic(ref set, ref queues);
            else
                FlushDeferredOpsNonDeterministic(ref set, ref queues);
        }

        static void FlushDeferredOpsNonDeterministic(
            ref EntitySetStorage set,
            ref NativeSetDeferredQueues queues
        )
        {
            for (int i = 0; i < queues.RemoveQueue.ThreadSlotCount; i++)
            {
                ref var bag = ref queues.RemoveQueue.GetBag(i);
                while (!bag.IsEmpty)
                    set.RemoveUnchecked(bag.Dequeue<EntityIndex>());
            }

            for (int i = 0; i < queues.AddQueue.ThreadSlotCount; i++)
            {
                ref var bag = ref queues.AddQueue.GetBag(i);
                while (!bag.IsEmpty)
                    set.AddUnchecked(bag.Dequeue<EntityIndex>());
            }
        }

        static void FlushDeferredOpsDeterministic(
            ref EntitySetStorage set,
            ref NativeSetDeferredQueues queues
        )
        {
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
            foreach (var setCollection in EntitySets)
            {
                setCollection.Value.Dispose();
            }

            foreach (var queues in DeferredQueues)
            {
                queues.Value.Dispose();
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
            SetIdsByGroup.Dispose();
        }

        /// <summary>
        /// Sets are automatically updated by the framework. If entities are removed
        /// from the database the sets are updated consequently.
        /// </summary>
        public void RemoveEntitiesFromSets(
            FastList<int> entityIndicesRemoved,
            GroupIndex fromGroup,
            DenseDictionary<int, int> entityIdsAffectedByRemoveAtSwapBack
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
                ref var setCollection = ref EntitySets.GetValueByRef(setIds[i]);

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
            DenseDictionary<int, MoveInfo> fromEntityToEntityIDs,
            GroupIndex fromGroup,
            GroupIndex toGroup,
            DenseDictionary<int, int> entityIdsAffectedByRemoveAtSwapBack
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
                ref var setCollection = ref EntitySets.GetValueByRef(setIds[i]);

                if (!setCollection.TryGetGroupEntry(fromGroup, out var fromGroupEntry))
                {
                    continue;
                }

                SetGroupEntry groupEntryTo = default;
                bool resolvedToGroup = false;

                var countOfEntitiesToSwap = fromEntityToEntityIDs.Count;
                MoveInfo[] moveInfosOfEntitiesToSwap = fromEntityToEntityIDs.UnsafeValues;
                var keysOfEntitiesToSwap = fromEntityToEntityIDs.UnsafeKeys;

                for (var index = 0; index < countOfEntitiesToSwap; index++)
                {
                    int fromEntityIndex = keysOfEntitiesToSwap[index].key;
                    int toIndex = (int)moveInfosOfEntitiesToSwap[index].ToIndex;

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

        // Every registered set matches at least one group (asserted in RegisterSet),
        // so "any registered set" is equivalent to the old "any routing entry".
        public bool HasAnySets => EntitySets.Count > 0;

        public EntityQuerier.TrecsSets GetTrecsSets()
        {
            return new EntityQuerier.TrecsSets(EntitySets);
        }
    }
}
