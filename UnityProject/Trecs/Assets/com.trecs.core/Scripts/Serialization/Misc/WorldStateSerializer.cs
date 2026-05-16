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
        void SerializeState(ISerializationWriter writer);
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
                    TrecsAssert.That(TypeIdProvider.IsRegistered(componentType));
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
                TrecsAssert.That(
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

        public void SerializeState(ISerializationWriter writer)
        {
            using (TrecsProfiling.Start("Serializing game state"))
            {
                SerializeImpl(writer);
                writer.Write("StreamGuard", WorldStateStreamGuard);
            }
        }

        void DeserializeStateImpl(ISerializationReader reader)
        {
            // Checksum streams skip VUO template component arrays via
            // ShouldSkip on the write side. They are not a valid input
            // for restore — the missing VUO entries would silently zero
            // out render-side state on load. Treat that as caller error
            // rather than corrupted data.
            TrecsAssert.That(
                !reader.HasFlag(SerializationFlags.IsForChecksum),
                "Cannot restore world state from a checksum-mode stream — VUO template component arrays are intentionally omitted in that mode."
            );

            var eventsManager = _world.GetEventsManager();

            using (TrecsProfiling.Start("Triggering OnEcsDeserializeStarted listeners"))
            {
                eventsManager.DeserializeStartedEvent.Invoke();
            }

            _world.GetUniqueHeap().ClearAll(warnUndisposed: false);
            _world.GetSharedHeap().ClearAll(warnUndisposed: false);
            _world.GetNativeSharedHeap().ClearAll(warnUndisposed: false);
            _world.GetNativeUniqueHeap().ClearAll(warnUndisposed: false);
            _world.GetTrecsListHeap().ClearAll(warnUndisposed: false);
            // Frame-scoped native unique entries share the chunk store with NativeUnique
            // and TrecsList. They must be cleared too so the chunk store deserialize sees
            // an empty store. They'll be re-populated when EntityInputQueue.Deserialize
            // runs later (orchestrated by the caller, e.g. BundlePlayer).
            _world.GetFrameScopedNativeUniqueHeap().ClearAll();

            DeserializeImpl(reader);

            var guard = reader.Read<int>("StreamGuard");
            TrecsAssert.IsEqual(guard, WorldStateStreamGuard);

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

        void WriteSets(
            in NativeDenseDictionary<SetId, EntitySetStorage> sets,
            ISerializationWriter writer
        )
        {
            var numSets = sets.Count;
            writer.Write("NumSets", numSets);

            for (int i = 0; i < numSets; i++)
            {
                writer.Write("SetId", sets.UnsafeKeys[i]);

                ref readonly var set = ref sets.UnsafeValues[i];
                var registeredGroups = set._registeredGroups;
                var entriesPerGroup = set._entriesPerGroup;

                var numGroups = registeredGroups.Length;
                writer.Write("NumGroups", numGroups);

                for (int k = 0; k < numGroups; k++)
                {
                    var group = registeredGroups[k];
                    writer.Write("Group", _worldDef.ToTagSet(group));

                    var groupEntities = entriesPerGroup[group.Index];

                    // This one looks ok to blit
                    writer.Write("EntityIdToDenseIndex", groupEntities._entityIdToDenseIndex);
                }
            }
        }

        unsafe void WriteEntityHandlesMapInternal(
            ISerializationWriter writer,
            in NativeList<EntityHandleMapElement> entityIdMap
        )
        {
            writer.Write("Count", entityIdMap.Length);

            writer.BlitWriteArrayPtr(
                "EntityIdMap",
                (EntityHandleMapElement*)entityIdMap.GetUnsafeReadOnlyPtr(),
                entityIdMap.Length
            );
        }

        void WriteEntityIndexToReferenceMap(
            ISerializationWriter writer,
            in NativeList<UnsafeList<int>> entityIndexToReferenceMap
        )
        {
            var count = entityIndexToReferenceMap.Length;

            writer.Write("Count", count);

            for (int i = 0; i < count; i++)
            {
                var tagSet = _worldDef.ToTagSet(GroupIndex.FromIndex(i));
                writer.Write("Group", tagSet);

                var list = entityIndexToReferenceMap[i];
                writer.Write("ListLength", list.Length);
                for (int j = 0; j < list.Length; j++)
                {
                    writer.Write("Ref", list[j]);
                }
            }
        }

        void WriteEntityHandlesMap(ISerializationWriter writer)
        {
            ref var entityHandlesMap = ref _world.GetEntityQuerier()._entityLocator;

            WriteEntityHandlesMapInternal(writer, entityHandlesMap._entityHandleMap);
            WriteEntityIndexToReferenceMap(writer, entityHandlesMap._entityIndexToReferenceMap);

            int nextFreeIndex = entityHandlesMap._nextFreeIndex;
            writer.Write("NextFreeIndex", nextFreeIndex);
        }

        /// <remarks>
        /// Variable-update components are skipped only when serializing for a
        /// checksum (they would always desync between runs). Snapshots and
        /// recordings include them, since otherwise the component arrays are
        /// left in an invalid state on the read side.
        ///
        /// Checksum data is only ever byte-compared, never deserialized, so
        /// the read path has no symmetric guard. If that ever changes, this
        /// guard's twin must be added there to keep the bit stream aligned.
        /// </remarks>
        bool ShouldSkip(GroupIndex group, ComponentId componentId, ISerializationWriter writer)
        {
            if (!writer.HasFlag(SerializationFlags.IsForChecksum))
            {
                return false;
            }

            var componentType = TypeIdProvider.GetTypeFromId(componentId.Value);

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
                var bytesBefore = writer.NumBytesWritten;

                var group = GroupIndex.FromIndex(i);
                writer.Write("Group", _worldDef.ToTagSet(group));

                var subMap = groupEntityComponentsDB[i];

                var numComponents = subMap.Count;

                writer.Write("NumComponents", numComponents);

                for (int k = 0; k < numComponents; k++)
                {
                    ComponentId componentId = subMap.UnsafeKeys[k].key;
                    writer.Write("ComponentId", componentId);

                    if (!ShouldSkip(group, componentId, writer))
                    {
                        IComponentArray unmanagedStrategy = subMap.UnsafeValues[k];
                        WriteComponentArray(unmanagedStrategy, writer);
                    }
                }

                _log.Trace(
                    "GroupIndex {0} serialized in {1} kb",
                    group,
                    (writer.NumBytesWritten - bytesBefore) / 1024f
                );
            }
        }

        void SerializeImpl(ISerializationWriter writer)
        {
            writer.Write("RngSeed", _world.FixedRng);
            writer.Write("FixedFrameCount", _world.SystemRunner._currentFixedFrameCount);
            writer.Write("FixedElapsedTime", _world.SystemRunner._elapsedFixedTime);

            var bytesBefore = writer.NumBytesWritten;

            using (TrecsProfiling.Start("Writing component arrays"))
            {
                WriteGroupEntityComponentsDB(writer);
            }

            _log.Trace(
                "Component arrays serialized in {0} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );
            bytesBefore = writer.NumBytesWritten;

            using (TrecsProfiling.Start("Writing entity references map"))
            {
                WriteEntityHandlesMap(writer);
            }

            _log.Trace(
                "Entity references map serialized in {0} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );

            bytesBefore = writer.NumBytesWritten;
            using (TrecsProfiling.Start("Writing entity sets"))
            {
                var setStore = _world.GetSetStore();

                WriteSets(setStore.EntitySets, writer);
                WriteSetRoutingIndex(setStore.SetIdsByGroup, writer);
            }
            _log.Trace(
                "Entity sets serialized in {0} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );

            bytesBefore = writer.NumBytesWritten;

            using (TrecsProfiling.Start("Writing heap memory"))
            {
                _world.GetUniqueHeap().Serialize(writer);
                _world.GetSharedHeap().Serialize(writer);
                _world.GetNativeSharedHeap().Serialize(writer);
                // Chunk store first: bulk-dump pages + side-table state. The two
                // chunk-store-backed heaps below then write only their managed-side
                // (handle → type) bookkeeping; FrameScopedNativeUniqueHeap is
                // serialized separately by EntityInputQueue (its entries are tied to
                // input data, not world state).
                _world.GetNativeUniqueChunkStore().Serialize(writer);
                _world.GetNativeUniqueHeap().Serialize(writer);
                _world.GetTrecsListHeap().Serialize(writer);
            }

            _log.Trace(
                "Heap memory serialized in {0} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );

            // Per-system deterministic paused state. Channel state (Editor /
            // Playback / User) is intentionally not serialized — those are
            // ephemeral, app-side concerns.
            using (TrecsProfiling.Start("Writing system enable state"))
            {
                _world.SystemEnableState.Serialize(writer);
            }
        }

        void WriteSetRoutingIndex(
            in NativeList<UnsafeList<SetId>> routingIndex,
            ISerializationWriter writer
        )
        {
            // Emit only non-empty slots, keeping the wire format sparse.
            int nonEmptyCount = 0;
            for (int i = 0; i < routingIndex.Length; i++)
            {
                if (routingIndex[i].Length > 0)
                    nonEmptyCount++;
            }
            writer.Write("NumRoutingEntries", nonEmptyCount);

            for (int i = 0; i < routingIndex.Length; i++)
            {
                var list = routingIndex[i];
                if (list.Length == 0)
                    continue;
                writer.Write("Group", _worldDef.ToTagSet(GroupIndex.FromIndex(i)));
                writer.Write("ListLength", list.Length);
                for (int j = 0; j < list.Length; j++)
                {
                    writer.Write("SetId", list[j]);
                }
            }
        }

        void ReadSetRoutingIndex(
            NativeList<UnsafeList<SetId>> routingIndex,
            ISerializationReader reader
        )
        {
            var numEntries = reader.Read<int>("NumRoutingEntries");

            // Clear existing contents of every slot (reset to empty).
            for (int i = 0; i < routingIndex.Length; i++)
            {
                routingIndex.ElementAt(i).Clear();
            }

            for (int i = 0; i < numEntries; i++)
            {
                var tagSet = reader.Read<TagSet>("Group");
                var group = _worldDef.ToGroupIndex(tagSet);
                var listLength = reader.Read<int>("ListLength");
                ref var list = ref routingIndex.ElementAt(group.Index);
                list.Resize(listLength, NativeArrayOptions.UninitializedMemory);
                for (int j = 0; j < listLength; j++)
                {
                    list[j] = reader.Read<SetId>("SetId");
                }
            }
        }

        void ReadSets(
            NativeDenseDictionary<SetId, EntitySetStorage> sets,
            ISerializationReader reader
        )
        {
            var numSets = reader.Read<int>("NumSets");
            TrecsAssert.That(numSets >= 0);

            TrecsAssert.IsEqual(sets.Count, numSets);

            for (int i = 0; i < numSets; i++)
            {
                var setId = reader.Read<SetId>("SetId");

                var currentSetId = sets.UnsafeKeys[i];
                TrecsAssert.IsEqual(setId, currentSetId);

                ref readonly var groupMap = ref sets.GetValueByRef(setId);

                var numGroups = reader.Read<int>("NumGroups");
                groupMap.Clear();

                for (int k = 0; k < numGroups; k++)
                {
                    var tagSet = reader.Read<TagSet>("Group");
                    var group = _worldDef.ToGroupIndex(tagSet);

                    var groupEntry = groupMap.GetSetGroupEntry(group);

                    // This one looks ok to blit
                    reader.Read("EntityIdToDenseIndex", ref groupEntry._entityIdToDenseIndex);
                }
            }
        }

        unsafe void ReadEntityHandlesMapInternal(
            ISerializationReader reader,
            ref NativeList<EntityHandleMapElement> entityIdMap
        )
        {
            var size = reader.Read<int>("Count");
            TrecsAssert.That(size >= 0);

            if (entityIdMap.Capacity < size)
            {
                entityIdMap.Capacity = size;
            }

            entityIdMap.ResizeUninitialized(size);

            reader.BlitReadArrayPtr(
                "EntityIdMap",
                (EntityHandleMapElement*)entityIdMap.GetUnsafePtr(),
                size
            );
        }

        void ReadEntityIndexToReferenceMap(
            ISerializationReader reader,
            NativeList<UnsafeList<int>> entityIndexToReferenceMap
        )
        {
            var count = reader.Read<int>("Count");
            TrecsAssert.That(count >= 0);

            TrecsAssert.IsEqual(count, entityIndexToReferenceMap.Length);

            for (int i = 0; i < count; i++)
            {
                var tagSet = reader.Read<TagSet>("Group");
                var group = _worldDef.ToGroupIndex(tagSet);

                ref var groupList = ref entityIndexToReferenceMap.ElementAt(group.Index);

                var listLength = reader.Read<int>("ListLength");
                groupList.Clear();
                groupList.Resize(listLength, NativeArrayOptions.ClearMemory);
                for (int j = 0; j < listLength; j++)
                {
                    groupList[j] = reader.Read<int>("Ref");
                }
            }
        }

        void ReadEntityHandlesMap(ISerializationReader reader)
        {
            ref var entityHandlesMap = ref _world.GetEntityQuerier()._entityLocator;

            ReadEntityHandlesMapInternal(reader, ref entityHandlesMap._entityHandleMap);
            ReadEntityIndexToReferenceMap(reader, entityHandlesMap._entityIndexToReferenceMap);

            var nextFreeIndex = reader.Read<int>("NextFreeIndex");
            TrecsAssert.That(nextFreeIndex >= 0);

            entityHandlesMap._nextFreeIndex.Set(nextFreeIndex);
        }

        void ReadGroupEntityComponentsDB(ISerializationReader reader)
        {
            var groupEntityComponentsDB = _world.ComponentStore.GroupEntityComponentsDB;

            var numItems = reader.Read<int>("Count");
            TrecsAssert.That(numItems >= 0);

            TrecsAssert.IsEqual(groupEntityComponentsDB.Length, numItems);

            for (int i = 0; i < numItems; i++)
            {
                var tagSet = reader.Read<TagSet>("Group");
                var group = _worldDef.ToGroupIndex(tagSet);

                TrecsAssert.IsEqual(GroupIndex.FromIndex(i), group);

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

                TrecsAssert.That(
                    subMap.Count == numComponents,
                    "Unexpected number of components for group {0}. Expected {1} (from snapshot), got {2} (in memory)",
                    group,
                    numComponents,
                    subMap.Count
                );

                for (int k = 0; k < numComponents; k++)
                {
                    var componentId = reader.Read<ComponentId>("ComponentId");
                    TrecsAssert.IsEqual(subMap.UnsafeKeys[k].key, componentId);
                    ReadComponentArray(subMap.UnsafeValues[k], reader);
                }
            }
        }

        void DeserializeImpl(ISerializationReader reader)
        {
            reader.ReadInPlace("rngSeed", _world.FixedRng);
            _world.SystemRunner._currentFixedFrameCount = reader.Read<int>("FixedFrameCount");
            _world.SystemRunner._elapsedFixedTime = reader.Read<float>("FixedElapsedTime");

            using (TrecsProfiling.Start("Reading component arrays"))
            {
                ReadGroupEntityComponentsDB(reader);
            }

            using (TrecsProfiling.Start("Reading entity references map"))
            {
                ReadEntityHandlesMap(reader);
            }

            using (TrecsProfiling.Start("Reading entity sets"))
            {
                var setStore = _world.GetSetStore();

                ReadSets(setStore.EntitySets, reader);
                ReadSetRoutingIndex(setStore.SetIdsByGroup, reader);
            }

            using (TrecsProfiling.Start("Reading heap memory"))
            {
                _world.GetUniqueHeap().Deserialize(reader);
                _world.GetSharedHeap().Deserialize(reader);
                _world.GetNativeSharedHeap().Deserialize(reader);
                // Chunk store first — must mirror the SerializeImpl order. Restores all
                // pages and side-table entries (including those that belong to
                // FrameScopedNativeUniqueHeap; that heap's _activeEntries gets re-linked
                // later by EntityInputQueue.Deserialize).
                _world.GetNativeUniqueChunkStore().Deserialize(reader);
                _world.GetNativeUniqueHeap().Deserialize(reader);
                _world.GetTrecsListHeap().Deserialize(reader);
            }

            using (TrecsProfiling.Start("Reading system enable state"))
            {
                _world.SystemEnableState.Deserialize(reader);
            }
        }

        // Distinct from SerializationConstants.EndOfPayloadMarker: that is a
        // byte written once at the very end of any payload. This int guards the
        // ECS-state section inside the world payload — if the ECS write/read
        // sequence drifts (a Serialize* forgot to match its Deserialize*) this
        // magic will mismatch and we fail loudly instead of silently reading
        // garbage into component arrays.
        const int WorldStateStreamGuard = 510120270;
    }
}
