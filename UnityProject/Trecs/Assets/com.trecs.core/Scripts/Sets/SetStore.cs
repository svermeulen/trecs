using System;
using System.ComponentModel;
using Trecs.Collections;
using Unity.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class SetStore : IDisposable
    {
        internal NativeDenseDictionary<SetId, EntitySet> EntitySets;
        internal NativeDenseDictionary<SetId, NativeSetDeferredQueues> DeferredQueues;

        // Routing index: maps Group to set IDs registered for that group.
        // When an entity in group G is removed/swapped, we look up all set IDs
        // registered under G and update them.
        internal NativeDenseDictionary<Group, NativeList<SetId>> SetIdsByGroup;

        public SetStore()
        {
            EntitySets = new NativeDenseDictionary<SetId, EntitySet>(0, Allocator.Persistent);
            DeferredQueues = new NativeDenseDictionary<SetId, NativeSetDeferredQueues>(
                0,
                Allocator.Persistent
            );
            SetIdsByGroup = new NativeDenseDictionary<Group, NativeList<SetId>>(
                0,
                Allocator.Persistent
            );
        }

        /// <summary>
        /// Registers a set during world initialization. Creates the EntitySet,
        /// pre-populates group entries, and populates the group-based routing index.
        /// </summary>
        public void RegisterSet(SetDef setDef, WorldInfo worldInfo)
        {
            Assert.That(
                !EntitySets.ContainsKey(setDef.Id),
                "Set '{}' is already registered",
                setDef.DebugName
            );

            var groups = setDef.Tags.IsNull
                ? worldInfo.AllGroups
                : worldInfo.GetGroupsWithTags(setDef.Tags);
            Assert.That(
                groups.Count > 0,
                "Set '{}' matched no groups. Are the tags used by a template added to the WorldBuilder?",
                setDef.DebugName
            );

            var validGroups = new DenseHashSet<Group>(groups.Count);
            foreach (var group in groups)
            {
                validGroups.Add(group);
            }

            EntitySets.Add(setDef.Id, new EntitySet(setDef.Id, validGroups));
            DeferredQueues.Add(
                setDef.Id,
                new NativeSetDeferredQueues(AtomicNativeBags.Create(), AtomicNativeBags.Create())
            );

            foreach (var group in groups)
            {
                if (!SetIdsByGroup.TryGetIndex(group, out var routingIdx))
                {
                    var newList = new NativeList<SetId>(1, Allocator.Persistent);
                    newList.Add(setDef.Id);
                    SetIdsByGroup.Add(group, newList);
                }
                else
                {
                    ref var list = ref SetIdsByGroup.GetValueAtIndexByRef(routingIdx);
                    list.Add(setDef.Id);
                }
            }
        }

        internal ref EntitySet GetSet(SetId setId)
        {
            var success = EntitySets.TryGetIndex(setId, out var index);
            Assert.That(
                success,
                "Set with ID '{}' not registered. Add it to the WorldBuilder via AddSet<T>().",
                setId
            );
            return ref EntitySets.GetValueAtIndexByRef(index);
        }

        internal ref EntitySet GetSet(SetDef setDef)
        {
            return ref EntitySets.GetValueByRef(setDef.Id);
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
            var sets = EntitySets.GetValuesWrite(out var count);
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
            ref EntitySet set,
            ref NativeSetDeferredQueues queues,
            bool requireDeterministic
        )
        {
            if (requireDeterministic)
                FlushDeferredOpsDeterministic(ref set, ref queues);
            else
                FlushDeferredOpsNonDeterministic(ref set, ref queues);
        }

        static void FlushDeferredOpsNonDeterministic(
            ref EntitySet set,
            ref NativeSetDeferredQueues queues
        )
        {
            for (int i = 0; i < queues.RemoveQueue.Count; i++)
            {
                ref var bag = ref queues.RemoveQueue.GetBag(i);
                while (!bag.IsEmpty())
                    set.RemoveImmediateUnchecked(bag.Dequeue<EntityIndex>());
            }

            for (int i = 0; i < queues.AddQueue.Count; i++)
            {
                ref var bag = ref queues.AddQueue.GetBag(i);
                while (!bag.IsEmpty())
                    set.AddImmediateUnchecked(bag.Dequeue<EntityIndex>());
            }
        }

        static void FlushDeferredOpsDeterministic(
            ref EntitySet set,
            ref NativeSetDeferredQueues queues
        )
        {
            var allRemoves = new NativeList<EntityIndex>(64, Allocator.Temp);
            for (int i = 0; i < queues.RemoveQueue.Count; i++)
            {
                ref var bag = ref queues.RemoveQueue.GetBag(i);
                while (!bag.IsEmpty())
                    allRemoves.Add(bag.Dequeue<EntityIndex>());
            }
            allRemoves.Sort();
            for (int i = 0; i < allRemoves.Length; i++)
                set.RemoveImmediateUnchecked(allRemoves[i]);
            allRemoves.Dispose();

            var allAdds = new NativeList<EntityIndex>(64, Allocator.Temp);
            for (int i = 0; i < queues.AddQueue.Count; i++)
            {
                ref var bag = ref queues.AddQueue.GetBag(i);
                while (!bag.IsEmpty())
                    allAdds.Add(bag.Dequeue<EntityIndex>());
            }
            allAdds.Sort();
            for (int i = 0; i < allAdds.Length; i++)
                set.AddImmediateUnchecked(allAdds[i]);
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
                queues.Value.AddQueue.Dispose();
                queues.Value.RemoveQueue.Dispose();
            }

            foreach (var entry in SetIdsByGroup)
            {
                entry.Value.Dispose();
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
            Group fromGroup,
            DenseDictionary<int, int> entityIdsAffectedByRemoveAtSwapBack
        )
        {
            if (!SetIdsByGroup.TryGetValue(fromGroup, out NativeList<SetId> setIds))
            {
                return;
            }

            var numberOfSets = setIds.Length;

            for (int i = 0; i < numberOfSets; ++i)
            {
                ref var setCollection = ref EntitySets.GetValueByRef(setIds[i]);

                if (!setCollection._entriesPerGroup.TryGetValue(fromGroup, out var fromGroupEntry))
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
            Group fromGroup,
            Group toGroup,
            DenseDictionary<int, int> entityIdsAffectedByRemoveAtSwapBack
        )
        {
            if (!SetIdsByGroup.TryGetValue(fromGroup, out NativeList<SetId> setIds))
            {
                return;
            }

            var numberOfSets = setIds.Length;

            for (int i = 0; i < numberOfSets; ++i)
            {
                ref var setCollection = ref EntitySets.GetValueByRef(setIds[i]);

                if (!setCollection._entriesPerGroup.TryGetValue(fromGroup, out var fromGroupEntry))
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

        public bool HasAnySets => SetIdsByGroup.Count > 0;

        public EntityQuerier.TrecsSets GetTrecsSets()
        {
            return new EntityQuerier.TrecsSets(EntitySets);
        }
    }
}
