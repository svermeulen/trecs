using System;
using System.Collections.Generic;
using System.ComponentModel;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class SetStore : IDisposable
    {
        NativeHashMap<SetId, EntitySetStorage> _entitySets;
        internal NativeHashMap<SetId, NativeSetDeferredQueues> DeferredQueues;
        internal NativeList<SetId> SetIds;

        // Routing index: per-group list of set IDs registered for that group,
        // indexed by GroupIndex.Index. When an entity in group G is
        // removed/swapped, we look up all set IDs registered under G and
        // update them. Inner is UnsafeList<SetId> so the outer NativeList holds
        // non-NativeContainer values — same pattern as EntityHandleMap's reverse map.
        [NativeDisableContainerSafetyRestriction]
        NativeList<UnsafeList<SetId>> _setIdsByGroup;

        public SetStore(int groupCount)
        {
            _entitySets = new NativeHashMap<SetId, EntitySetStorage>(0, Allocator.Persistent);
            DeferredQueues = new NativeHashMap<SetId, NativeSetDeferredQueues>(
                0,
                Allocator.Persistent
            );
            SetIds = new NativeList<SetId>(0, Allocator.Persistent);
            _setIdsByGroup = new NativeList<UnsafeList<SetId>>(groupCount, Allocator.Persistent);
            _setIdsByGroup.Resize(groupCount, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < groupCount; i++)
            {
                _setIdsByGroup[i] = new UnsafeList<SetId>(0, Allocator.Persistent);
            }
        }

        /// <summary>
        /// Registers a set during world initialization. Creates the EntitySetStorage,
        /// pre-populates group entries, and populates the group-based routing index.
        /// </summary>
        public void RegisterSet(EntitySet entitySet, WorldInfo worldInfo)
        {
            TrecsDebugAssert.That(
                !_entitySets.ContainsKey(entitySet.Id),
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

            _entitySets.Add(
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
                ref var list = ref _setIdsByGroup.ElementAt(group.Index);
                list.Add(entitySet.Id);
            }
        }

        internal EntitySetStorage GetSet(SetId setId)
        {
            var found = _entitySets.TryGetValue(setId, out var result);
            TrecsDebugAssert.That(
                found,
                "Set with ID '{0}' not registered. Add it to the WorldBuilder via AddSet<T>().",
                setId
            );
            return result;
        }

        internal EntitySetStorage GetSet(EntitySet entitySet)
        {
            return _entitySets[entitySet.Id];
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
                _entitySets[SetIds[i]].FlushJobWrites();
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
                var set = _entitySets[setId];
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
                _entitySets[setId].Dispose();
                DeferredQueues[setId].Dispose();
            }

            for (int i = 0; i < _setIdsByGroup.Length; i++)
            {
                ref var list = ref _setIdsByGroup.ElementAt(i);
                if (list.IsCreated)
                {
                    list.Dispose();
                }
            }

            _entitySets.Dispose();
            DeferredQueues.Dispose();
            SetIds.Dispose();
            _setIdsByGroup.Dispose();
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
            var setIds = _setIdsByGroup[fromGroup.Index];
            var numberOfSets = setIds.Length;
            if (numberOfSets == 0)
            {
                return;
            }

            for (int i = 0; i < numberOfSets; ++i)
            {
                var setCollection = _entitySets[setIds[i]];

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

        /// <summary>
        /// Removes the given departed entity indices from every set the group
        /// participates in, without touching survivor positions. Used to defer set
        /// removal until after OnRemoved callbacks for whole-group removals (where
        /// there are no survivors and therefore no swap-back re-keying to do).
        /// </summary>
        public void RemoveDepartedEntitiesFromSets(
            List<int> entityIndicesRemoved,
            GroupIndex fromGroup
        )
        {
            var setIds = _setIdsByGroup[fromGroup.Index];
            var numberOfSets = setIds.Length;
            if (numberOfSets == 0)
            {
                return;
            }

            for (int i = 0; i < numberOfSets; ++i)
            {
                var setCollection = _entitySets[setIds[i]];

                if (!setCollection.TryGetGroupEntry(fromGroup, out var fromGroupEntry))
                {
                    continue;
                }

                var entitiesCount = entityIndicesRemoved.Count;
                for (int entityIndex = 0; entityIndex < entitiesCount; ++entityIndex)
                {
                    fromGroupEntry.Remove(entityIndicesRemoved[entityIndex]);
                }
            }
        }

        /// <summary>
        /// Clears the given group's entries from every set the group participates
        /// in. Used by the whole-group removal fast path, where the entire group is
        /// removed in one batch (no survivors) so the group's set entry can be
        /// cleared wholesale instead of removing each departed index individually.
        /// </summary>
        public void ClearGroupFromSets(GroupIndex group)
        {
            var setIds = _setIdsByGroup[group.Index];
            var numberOfSets = setIds.Length;
            if (numberOfSets == 0)
            {
                return;
            }

            for (int i = 0; i < numberOfSets; ++i)
            {
                var setCollection = _entitySets[setIds[i]];

                if (setCollection.TryGetGroupEntry(group, out var groupEntry))
                {
                    groupEntry.Clear();
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
            var setIds = _setIdsByGroup[fromGroup.Index];
            var numberOfSets = setIds.Length;
            if (numberOfSets == 0)
            {
                return;
            }

            for (int i = 0; i < numberOfSets; ++i)
            {
                var setCollection = _entitySets[setIds[i]];

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

        /// <summary>
        /// Writes all set-membership state (per-set group entries plus the
        /// group→set routing index) to the snapshot stream. The wire format
        /// lives here, next to the storage it mirrors;
        /// <see cref="WorldStateSerializer"/> only owns section ordering.
        /// </summary>
        public void Serialize(ISerializationWriter writer, WorldInfo worldInfo)
        {
            WriteSets(writer, worldInfo);
            WriteRoutingIndex(writer, worldInfo);
        }

        public void Deserialize(ISerializationReader reader, WorldInfo worldInfo)
        {
            ReadSets(reader, worldInfo);
            ReadRoutingIndex(reader, worldInfo);
        }

        void WriteSets(ISerializationWriter writer, WorldInfo worldInfo)
        {
            var numSets = SetIds.Length;
            writer.Write("NumSets", numSets);

            for (int i = 0; i < numSets; i++)
            {
                writer.PushScope("Set{0}", i);
                writer.Write("SetId", SetIds[i]);

                var set = _entitySets[SetIds[i]];
                var registeredGroups = set._registeredGroups;
                var entriesPerGroup = set._entriesPerGroup;

                var numGroups = registeredGroups.Length;
                writer.Write("NumGroups", numGroups);

                for (int k = 0; k < numGroups; k++)
                {
                    writer.PushScope("Group{0}", k);
                    var group = registeredGroups[k];
                    writer.Write("Group", worldInfo.ToTagSet(group));

                    var groupEntities = entriesPerGroup[group.Index];

                    writer.Write("EntityIdToDenseIndex", groupEntities._entityIdToDenseIndex);
                    writer.PopScope();
                }
                writer.PopScope();
            }
        }

        void ReadSets(ISerializationReader reader, WorldInfo worldInfo)
        {
            var numSets = reader.Read<int>("NumSets");
            TrecsDebugAssert.That(numSets >= 0);

            TrecsDebugAssert.IsEqual(SetIds.Length, numSets);

            for (int i = 0; i < numSets; i++)
            {
                var setId = reader.Read<SetId>("SetId");

                var currentSetId = SetIds[i];
                TrecsDebugAssert.IsEqual(setId, currentSetId);

                var groupMap = _entitySets[setId];

                var numGroups = reader.Read<int>("NumGroups");
                TrecsDebugAssert.IsEqual(groupMap._registeredGroups.Length, numGroups);
                groupMap.Clear();

                for (int k = 0; k < numGroups; k++)
                {
                    var tagSet = reader.Read<TagSet>("Group");
                    var group = worldInfo.ToGroupIndex(tagSet);

                    var groupEntry = groupMap.GetSetGroupEntry(group);

                    reader.Read("EntityIdToDenseIndex", ref groupEntry._entityIdToDenseIndex);
                }
            }
        }

        void WriteRoutingIndex(ISerializationWriter writer, WorldInfo worldInfo)
        {
            writer.PushScope("RoutingIndex");
            // Emit only non-empty slots, keeping the wire format sparse.
            int nonEmptyCount = 0;
            for (int i = 0; i < _setIdsByGroup.Length; i++)
            {
                if (_setIdsByGroup[i].Length > 0)
                    nonEmptyCount++;
            }
            writer.Write("NumRoutingEntries", nonEmptyCount);

            int entryIndex = 0;
            for (int i = 0; i < _setIdsByGroup.Length; i++)
            {
                var list = _setIdsByGroup[i];
                if (list.Length == 0)
                    continue;
                writer.PushScope("Entry{0}", entryIndex);
                writer.Write("Group", worldInfo.ToTagSet(GroupIndex.FromIndex(i)));
                writer.Write("SetIds", in list);
                writer.PopScope();
                entryIndex++;
            }
            writer.PopScope();
        }

        void ReadRoutingIndex(ISerializationReader reader, WorldInfo worldInfo)
        {
            var numEntries = reader.Read<int>("NumRoutingEntries");

            // Clear existing contents of every slot (reset to empty). Slots
            // present in the snapshot are overwritten by the Resize inside
            // UnsafeListSerializer; this pass handles the sparse slots that
            // the snapshot omits entirely.
            for (int i = 0; i < _setIdsByGroup.Length; i++)
            {
                _setIdsByGroup.ElementAt(i).Clear();
            }

            for (int i = 0; i < numEntries; i++)
            {
                var tagSet = reader.Read<TagSet>("Group");
                var group = worldInfo.ToGroupIndex(tagSet);
                ref var list = ref _setIdsByGroup.ElementAt(group.Index);
                reader.Read("SetIds", ref list);
            }
        }

        public bool HasAnySets => SetIds.Length > 0;
    }
}
