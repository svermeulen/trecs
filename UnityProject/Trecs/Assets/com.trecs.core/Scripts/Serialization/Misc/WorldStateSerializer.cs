using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Serializes or deserializes the full game state. Implemented by
    /// <see cref="WorldStateSerializer"/>; game code that needs to add its
    /// own data (e.g. scripting VM state) can implement this interface via
    /// composition around a <see cref="WorldStateSerializer"/>.
    /// </summary>
    public interface IWorldStateSerializer
    {
        void SerializeFullState(ISerializationWriter writer);
        void DeserializeState(ISerializationReader reader);
    }

    /// <summary>
    /// Serializes/deserializes the entire game state of a <see cref="World"/>.
    /// To override how a particular component type's array is serialized
    /// (e.g. to skip transient state or reset a runtime handle on load),
    /// register an <see cref="IComponentArraySerializer{T}"/> via
    /// <see cref="World.ComponentArraySerializerRegistry"/>.
    /// </summary>
    public sealed class WorldStateSerializer : IWorldStateSerializer
    {
        readonly TrecsLog _log;

        readonly World _world;
        readonly WorldInfo _worldDef;

        public WorldStateSerializer(World world)
        {
            _log = world.Log;
            _worldDef = world.WorldInfo;
            _world = world;

#if DEBUG && !TRECS_IS_PROFILING
            RegisterComponentTypeIds(_worldDef);
#endif
        }

#if DEBUG && !TRECS_IS_PROFILING
        static void RegisterComponentTypeIds(WorldInfo worldInfo)
        {
            foreach (var template in worldInfo.ResolvedTemplates)
            {
                foreach (var componentDec in template.ComponentDeclarations)
                {
                    var componentType = componentDec.ComponentType;
                    TrecsDebugAssert.That(TypeId.IsRegistered(componentType));
                }
            }
        }
#endif

        void WriteComponentArray(IComponentArray array, ISerializationWriter writer)
        {
            var count = array.Count;
            writer.Write("Count", count);

            if (
                _world.ComponentArraySerializerRegistry.TryGetDispatcher(
                    array.ComponentType,
                    out var dispatcher
                )
            )
            {
                writer.WriteBit(true);
                dispatcher.Serialize(array, writer);
                return;
            }

            writer.WriteBit(false);
            if (count > 0)
            {
                unsafe
                {
                    writer.BlitWriteRawBytes(
                        "Values",
                        array.GetUnsafePtr(),
                        array.ElementSize * count
                    );
                }
            }
        }

        void ReadComponentArray(IComponentArray array, ISerializationReader reader)
        {
            var count = reader.Read<int>("Count");
            bool isCustom = reader.ReadBit();
            if (isCustom)
            {
                var ok = _world.ComponentArraySerializerRegistry.TryGetDispatcher(
                    array.ComponentType,
                    out var dispatcher
                );
                TrecsDebugAssert.That(
                    ok,
                    "Stream marks a custom serializer for component type {0} but none is registered on this world",
                    array.ComponentType
                );
                dispatcher.Deserialize(array, count, reader);
                return;
            }

            array.Clear();
            if (count > 0)
            {
                array.EnsureCapacity(count);
                unsafe
                {
                    reader.BlitReadRawBytes(
                        "Values",
                        array.GetUnsafePtr(),
                        array.ElementSize * count
                    );
                }
            }
            array.SetCount(count);
        }

        public void SerializeFullState(ISerializationWriter writer)
        {
            using (TrecsProfiling.Start("Serializing game state"))
            {
                SerializeImpl(writer);
            }
        }

        void DeserializeStateImpl(ISerializationReader reader)
        {
            var eventsManager = _world.GetEventsManager();

            using (TrecsProfiling.Start("Triggering OnEcsDeserializeStarted listeners"))
            {
                eventsManager.DeserializeStartedEvent.Invoke();
            }

            _world.GetUniqueHeap().ClearAll(warnUndisposed: false);
            _world.GetSharedHeap().ClearAll(warnUndisposed: false);
            _world.GetNativeSharedHeap().ClearAll(warnUndisposed: false);
            // Persistent NativeUniquePtr / TrecsList allocations live entirely in
            // the chunk store. The chunk-store wipe inside
            // NativeHeap.Deserialize is sufficient to reset their state
            // before re-populating from the snapshot. Input-allocated unmanaged
            // data lives in InputNativeUniqueHeap's own per-allocation buffers,
            // not this chunk store, so no cleanup is needed here for that heap.

            DeserializeImpl(reader);

            _world.SystemRunner.OnEcsDeserializeCompleted();

            using (TrecsProfiling.Start("Triggering DeserializeCompletedEvent listeners"))
            {
                eventsManager.DeserializeCompletedEvent.Invoke();
            }
        }

        public void DeserializeState(ISerializationReader reader)
        {
            using (TrecsProfiling.Start("State Deserialization"))
            {
                DeserializeStateImpl(reader);
            }
        }

        void WriteSets(SetStore setStore, ISerializationWriter writer)
        {
            var setIds = setStore.SetIds;
            var sets = setStore.EntitySets;
            var numSets = setIds.Length;
            writer.Write("NumSets", numSets);

            for (int i = 0; i < numSets; i++)
            {
                writer.PushScope("Set{0}", i);
                writer.Write("SetId", setIds[i]);

                var set = sets[setIds[i]];
                var registeredGroups = set._registeredGroups;
                var entriesPerGroup = set._entriesPerGroup;

                var numGroups = registeredGroups.Length;
                writer.Write("NumGroups", numGroups);

                for (int k = 0; k < numGroups; k++)
                {
                    writer.PushScope("Group{0}", k);
                    var group = registeredGroups[k];
                    writer.Write("Group", _worldDef.ToTagSet(group));

                    var groupEntities = entriesPerGroup[group.Index];

                    writer.Write("EntityIdToDenseIndex", groupEntities._entityIdToDenseIndex);
                    writer.PopScope();
                }
                writer.PopScope();
            }
        }

        void WriteEntityIndexToReferenceMap(
            in NativeList<UnsafeList<int>> entityIndexToReferenceMap,
            ISerializationWriter writer
        )
        {
            var count = entityIndexToReferenceMap.Length;

            writer.Write("Count", count);

            for (int i = 0; i < count; i++)
            {
                writer.PushScope("Ref{0}", i);
                var tagSet = _worldDef.ToTagSet(GroupIndex.FromIndex(i));
                writer.Write("Group", tagSet);

                writer.Write("Refs", entityIndexToReferenceMap[i]);
                writer.PopScope();
            }
        }

        void WriteEntityHandlesMap(ISerializationWriter writer)
        {
            ref var entityHandlesMap = ref _world.GetEntityQuerier()._entityLocator;

            writer.Write("EntityIdMap", in entityHandlesMap._entityHandleMap);

            writer.PushScope("ReferenceMap");
            WriteEntityIndexToReferenceMap(entityHandlesMap._entityIndexToReferenceMap, writer);
            writer.PopScope();

            int nextFreeIndex = entityHandlesMap._nextFreeIndex;
            writer.Write("NextFreeIndex", nextFreeIndex);
        }

        bool ShouldSkip(GroupIndex group, TypeId componentId)
        {
            var componentType = TypeId.ToType(new TypeId(componentId.Value));
            var template = _worldDef.GetResolvedTemplateForGroup(group);
            var componentDec = template.GetComponentDeclaration(componentType);
            return template.IsVariableUpdateOnly(componentDec);
        }

        void WriteGroupEntityComponentsDB(ISerializationWriter writer)
        {
            var groupEntityComponentsDB = _world.ComponentStore.GroupEntityComponentsDB;

            var numItems = groupEntityComponentsDB.Length;

            writer.Write("Count", numItems);

            for (int i = 0; i < numItems; i++)
            {
                writer.PushScope("Group{0}", i);
                var bytesBefore = writer.NumBytesWritten;

                var group = GroupIndex.FromIndex(i);
                writer.Write("Group", _worldDef.ToTagSet(group));

                var subMap = groupEntityComponentsDB[i];

                var numComponents = subMap.Count;

                // Component-array slots are materialized lazily on first
                // entity. A group can end up with populated slots but zero
                // entities (e.g. after deserialization preallocates the
                // schema). This materialization state is a non-observable
                // implementation detail — treat an all-empty group
                // identically to a never-materialized one so serialized
                // bytes stay stable across recording/playback.
                if (numComponents > 0)
                {
                    var allEmpty = true;
                    for (int k = 0; k < numComponents; k++)
                    {
                        if (subMap.UnsafeValues[k].Count > 0)
                        {
                            allEmpty = false;
                            break;
                        }
                    }

                    if (allEmpty)
                    {
                        numComponents = 0;
                    }
                }

                writer.Write("NumComponents", numComponents);

                for (int k = 0; k < numComponents; k++)
                {
                    writer.PushScope("Component{0}", k);
                    TypeId componentId = subMap.UnsafeKeys[k].Key;
                    writer.Write("TypeId", componentId);

                    IComponentArray componentArray = subMap.UnsafeValues[k];

                    if (ShouldSkip(group, componentId))
                    {
                        writer.Write("Count", componentArray.Count);
                    }
                    else
                    {
                        WriteComponentArray(componentArray, writer);
                    }
                    writer.PopScope();
                }

                _log.Trace(
                    "GroupIndex {0} serialized in {1} kb",
                    group,
                    (writer.NumBytesWritten - bytesBefore) / 1024f
                );
                writer.PopScope();
            }
        }

        void SerializeImpl(ISerializationWriter writer)
        {
            writer.PushScope("World");

            writer.Write("RngSeed", _world.FixedRng);
            writer.Write("FixedFrameCount", _world.SystemRunner._currentFixedFrameCount);
            writer.Write("FixedElapsedTime", _world.SystemRunner._elapsedFixedTime);
            WriteSectionGuard(WorldStateSection.AfterTimingFields, writer);

            var bytesBefore = writer.NumBytesWritten;

            writer.PushScope("ComponentArrays");
            using (TrecsProfiling.Start("Writing component arrays"))
            {
                WriteGroupEntityComponentsDB(writer);
            }
            writer.PopScope();
            WriteSectionGuard(WorldStateSection.AfterComponentArrays, writer);

            _log.Trace(
                "Component arrays serialized in {0} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );
            bytesBefore = writer.NumBytesWritten;

            writer.PushScope("EntityHandles");
            using (TrecsProfiling.Start("Writing entity references map"))
            {
                WriteEntityHandlesMap(writer);
            }
            writer.PopScope();
            WriteSectionGuard(WorldStateSection.AfterEntityHandles, writer);

            _log.Trace(
                "Entity references map serialized in {0} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );

            bytesBefore = writer.NumBytesWritten;
            writer.PushScope("EntitySets");
            using (TrecsProfiling.Start("Writing entity sets"))
            {
                var setStore = _world.GetSetStore();

                WriteSets(setStore, writer);
                WriteSetRoutingIndex(setStore.SetIdsByGroup, writer);
            }
            writer.PopScope();
            WriteSectionGuard(WorldStateSection.AfterEntitySets, writer);
            _log.Trace(
                "Entity sets serialized in {0} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );

            bytesBefore = writer.NumBytesWritten;

            writer.PushScope("Heaps");
            using (TrecsProfiling.Start("Writing heap memory"))
            {
                writer.PushScope("UniqueHeap");
                _world.GetUniqueHeap().Serialize(writer);
                writer.PopScope();
                writer.PushScope("SharedHeap");
                _world.GetSharedHeap().Serialize(writer);
                writer.PopScope();
                writer.PushScope("NativeSharedHeap");
                _world.GetNativeSharedHeap().Serialize(writer);
                writer.PopScope();
                writer.PushScope("NativeUniqueChunkStore");
                _world.GetNativeUniqueChunkStore().Serialize(writer);
                writer.PopScope();
            }
            writer.PopScope();
            WriteSectionGuard(WorldStateSection.AfterHeaps, writer);

            _log.Trace(
                "Heap memory serialized in {0} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );

            writer.PushScope("SystemEnableState");
            using (TrecsProfiling.Start("Writing system enable state"))
            {
                _world.SystemEnableState.Serialize(writer);
            }
            writer.PopScope();
            WriteSectionGuard(WorldStateSection.AfterSystemEnable, writer);

            writer.PopScope();
        }

        void WriteSetRoutingIndex(
            in NativeList<UnsafeList<SetId>> routingIndex,
            ISerializationWriter writer
        )
        {
            writer.PushScope("RoutingIndex");
            // Emit only non-empty slots, keeping the wire format sparse.
            int nonEmptyCount = 0;
            for (int i = 0; i < routingIndex.Length; i++)
            {
                if (routingIndex[i].Length > 0)
                    nonEmptyCount++;
            }
            writer.Write("NumRoutingEntries", nonEmptyCount);

            int entryIndex = 0;
            for (int i = 0; i < routingIndex.Length; i++)
            {
                var list = routingIndex[i];
                if (list.Length == 0)
                    continue;
                writer.PushScope("Entry{0}", entryIndex);
                writer.Write("Group", _worldDef.ToTagSet(GroupIndex.FromIndex(i)));
                writer.Write("SetIds", in list);
                writer.PopScope();
                entryIndex++;
            }
            writer.PopScope();
        }

        void ReadSetRoutingIndex(
            NativeList<UnsafeList<SetId>> routingIndex,
            ISerializationReader reader
        )
        {
            var numEntries = reader.Read<int>("NumRoutingEntries");

            // Clear existing contents of every slot (reset to empty). Slots
            // present in the snapshot are overwritten by the Resize inside
            // UnsafeListSerializer; this pass handles the sparse slots that
            // the snapshot omits entirely.
            for (int i = 0; i < routingIndex.Length; i++)
            {
                routingIndex.ElementAt(i).Clear();
            }

            for (int i = 0; i < numEntries; i++)
            {
                var tagSet = reader.Read<TagSet>("Group");
                var group = _worldDef.ToGroupIndex(tagSet);
                ref var list = ref routingIndex.ElementAt(group.Index);
                reader.Read("SetIds", ref list);
            }
        }

        void ReadSets(SetStore setStore, ISerializationReader reader)
        {
            var setIds = setStore.SetIds;
            var sets = setStore.EntitySets;
            var numSets = reader.Read<int>("NumSets");
            TrecsDebugAssert.That(numSets >= 0);

            TrecsDebugAssert.IsEqual(setIds.Length, numSets);

            for (int i = 0; i < numSets; i++)
            {
                var setId = reader.Read<SetId>("SetId");

                var currentSetId = setIds[i];
                TrecsDebugAssert.IsEqual(setId, currentSetId);

                var groupMap = sets[setId];

                var numGroups = reader.Read<int>("NumGroups");
                groupMap.Clear();

                for (int k = 0; k < numGroups; k++)
                {
                    var tagSet = reader.Read<TagSet>("Group");
                    var group = _worldDef.ToGroupIndex(tagSet);

                    var groupEntry = groupMap.GetSetGroupEntry(group);

                    reader.Read("EntityIdToDenseIndex", ref groupEntry._entityIdToDenseIndex);
                }
            }
        }

        void ReadEntityIndexToReferenceMap(
            NativeList<UnsafeList<int>> entityIndexToReferenceMap,
            ISerializationReader reader
        )
        {
            var count = reader.Read<int>("Count");
            TrecsDebugAssert.That(count >= 0);

            TrecsDebugAssert.IsEqual(count, entityIndexToReferenceMap.Length);

            for (int i = 0; i < count; i++)
            {
                var tagSet = reader.Read<TagSet>("Group");
                var group = _worldDef.ToGroupIndex(tagSet);

                ref var groupList = ref entityIndexToReferenceMap.ElementAt(group.Index);
                reader.Read("Refs", ref groupList);
            }
        }

        void ReadEntityHandlesMap(ISerializationReader reader)
        {
            ref var entityHandlesMap = ref _world.GetEntityQuerier()._entityLocator;

            reader.Read("EntityIdMap", ref entityHandlesMap._entityHandleMap);
            ReadEntityIndexToReferenceMap(entityHandlesMap._entityIndexToReferenceMap, reader);

            var nextFreeIndex = reader.Read<int>("NextFreeIndex");
            TrecsDebugAssert.That(nextFreeIndex >= 0);

            entityHandlesMap._nextFreeIndex = nextFreeIndex;
        }

        void ReadGroupEntityComponentsDB(ISerializationReader reader)
        {
            var groupEntityComponentsDB = _world.ComponentStore.GroupEntityComponentsDB;

            var numItems = reader.Read<int>("Count");
            TrecsDebugAssert.That(numItems >= 0);

            TrecsDebugAssert.IsEqual(groupEntityComponentsDB.Length, numItems);

            for (int i = 0; i < numItems; i++)
            {
                var tagSet = reader.Read<TagSet>("Group");
                var group = _worldDef.ToGroupIndex(tagSet);

                TrecsDebugAssert.IsEqual(GroupIndex.FromIndex(i), group);

                var subMap = groupEntityComponentsDB[i];

                var numComponents = reader.Read<int>("NumComponents");

                // Per-group component slots are normally lazy (created on
                // first entity). Snapshot/recording reads need them in place
                // so the wire-format walk lines up with the in-memory map —
                // materialize them eagerly here for the group we're about
                // to populate.
                if (numComponents > 0 && subMap.Count == 0)
                {
                    var template = _worldDef.GetResolvedTemplateForGroup(group);
                    _world.ComponentStore.PreallocateDBGroup(group, 0, template.ComponentBuilders);
                }
                else if (numComponents == 0 && subMap.Count > 0)
                {
                    // Snapshot pre-dates this group's first entity, but the
                    // live world has since materialized it. Empty the arrays
                    // so the group is logically empty at the restored frame.
                    // Slots themselves stay (queries iterate Count, so an
                    // empty-arrays group is observationally identical to a
                    // never-materialized one) and there's nothing more to
                    // read for this group on the wire.
                    for (int k = 0; k < subMap.Count; k++)
                    {
                        subMap.UnsafeValues[k].Clear();
                        subMap.UnsafeValues[k].SetCount(0);
                    }
                    continue;
                }

                TrecsDebugAssert.That(
                    subMap.Count == numComponents,
                    "Unexpected number of components for group {0}. Expected {1} (from snapshot), got {2} (in memory)",
                    group,
                    numComponents,
                    subMap.Count
                );

                for (int k = 0; k < numComponents; k++)
                {
                    var componentId = reader.Read<TypeId>("TypeId");
                    TrecsDebugAssert.IsEqual(subMap.UnsafeKeys[k].Key, componentId);

                    if (ShouldSkip(group, componentId))
                    {
                        var count = reader.Read<int>("Count");
                        var arr = subMap.UnsafeValues[k];
                        arr.ResetToDefaultValuesWithCount(count);
                        continue;
                    }

                    ReadComponentArray(subMap.UnsafeValues[k], reader);
                }
            }
        }

        void DeserializeImpl(ISerializationReader reader)
        {
            reader.ReadInPlace("rngSeed", _world.FixedRng);
            _world.SystemRunner._currentFixedFrameCount = reader.Read<int>("FixedFrameCount");
            _world.SystemRunner._elapsedFixedTime = reader.Read<float>("FixedElapsedTime");
            ReadSectionGuard(WorldStateSection.AfterTimingFields, reader);

            using (TrecsProfiling.Start("Reading component arrays"))
            {
                ReadGroupEntityComponentsDB(reader);
            }
            ReadSectionGuard(WorldStateSection.AfterComponentArrays, reader);

            using (TrecsProfiling.Start("Reading entity references map"))
            {
                ReadEntityHandlesMap(reader);
            }
            ReadSectionGuard(WorldStateSection.AfterEntityHandles, reader);

            using (TrecsProfiling.Start("Reading entity sets"))
            {
                var setStore = _world.GetSetStore();

                ReadSets(setStore, reader);
                ReadSetRoutingIndex(setStore.SetIdsByGroup, reader);
            }
            ReadSectionGuard(WorldStateSection.AfterEntitySets, reader);

            using (TrecsProfiling.Start("Reading heap memory"))
            {
                _world.GetUniqueHeap().Deserialize(reader);
                _world.GetSharedHeap().Deserialize(reader);
                _world.GetNativeSharedHeap().Deserialize(reader);
                // Chunk-store deserialize restores all pages + side-table entries,
                // which is all persistent NativeUniquePtr / TrecsList allocations need
                // — both live entirely in the chunk store. Input-allocated unmanaged
                // data is owned by InputNativeUniqueHeap (not the chunk store) and
                // is not part of the snapshot payload.
                _world.GetNativeUniqueChunkStore().Deserialize(reader);
            }
            ReadSectionGuard(WorldStateSection.AfterHeaps, reader);

            using (TrecsProfiling.Start("Reading system enable state"))
            {
                _world.SystemEnableState.Deserialize(reader);
            }
            ReadSectionGuard(WorldStateSection.AfterSystemEnable, reader);
        }

        // Section markers written between major blocks of the world-state
        // stream. Drift between a Serialize* and its Deserialize* counterpart
        // is caught at the section boundary instead of cascading as garbage
        // into the next block, and the assert message pinpoints which section
        // diverged. One byte each — total cost is six bytes per snapshot.
        // Values are chosen out of the natural range of small ints/lengths
        // that surround them, so they're obvious in a hex dump.
        enum WorldStateSection : byte
        {
            AfterTimingFields = 0xA1,
            AfterComponentArrays = 0xA2,
            AfterEntityHandles = 0xA3,
            AfterEntitySets = 0xA4,
            AfterHeaps = 0xA5,
            AfterSystemEnable = 0xA6,
        }

        static void WriteSectionGuard(WorldStateSection section, ISerializationWriter writer)
        {
            writer.BlitWrite("SectionGuard", (byte)section);
        }

        static void ReadSectionGuard(WorldStateSection expected, ISerializationReader reader)
        {
            byte actual = 0;
            reader.BlitRead("SectionGuard", ref actual);
            if (actual != (byte)expected)
            {
                // SerializationException (not TrecsDebugAssert) because drift
                // detection is load-bearing in release builds too — letting a
                // misaligned bit stream cascade silently into the next section
                // would produce garbage entity-array reads, which is much
                // worse than a clean fail-loud here.
                throw new SerializationException(
                    $"WorldStateSerializer stream drift: expected section guard "
                        + $"{expected} (0x{(byte)expected:X2}) but got 0x{actual:X2}. "
                        + "A SerializeImpl section's wire format does not match its "
                        + "DeserializeImpl counterpart."
                );
            }
        }
    }
}
