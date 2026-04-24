using System;
using System.Collections.Generic;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Serialization
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
    /// Optional class that can be used to serialize/deserialize the entire game state.
    /// </summary>
    public class WorldStateSerializer : IWorldStateSerializer
    {
        static readonly TrecsLog _log = new(nameof(WorldStateSerializer));

        readonly World _world;
        readonly WorldInfo _worldDef;
        readonly Dictionary<Type, IComponentArrayCustomSerializer> _customComponentSerializers =
            new();

        public WorldStateSerializer(World world)
        {
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
                    Assert.That(TypeIdProvider.IsRegistered(componentType));
                }
            }
        }
#endif

        public void RegisterCustomComponentSerializer<T>(IComponentArrayCustomSerializer serializer)
            where T : unmanaged, IEntityComponent
        {
            _customComponentSerializers.Add(typeof(ComponentArray<T>), serializer);
        }

        void WriteComponentArray(IComponentArray array, ISerializationWriter writer)
        {
            if (_customComponentSerializers.TryGetValue(array.GetType(), out var custom))
            {
                writer.WriteBit(true);
                custom.Serialize(array, writer);
                return;
            }

            writer.WriteBit(false);
            writer.Write("count", array.Count);
            if (array.Count > 0)
            {
                unsafe
                {
                    writer.BlitWriteRawBytes(
                        "values",
                        array.GetUnsafePtr(),
                        array.ElementSize * array.Count
                    );
                }
            }
        }

        void ReadComponentArray(IComponentArray array, ISerializationReader reader)
        {
            bool isCustom = reader.ReadBit();
            if (isCustom)
            {
                var custom = _customComponentSerializers[array.GetType()];
                custom.Deserialize(array, reader);
                return;
            }

            var count = reader.Read<int>("count");
            array.Clear();
            if (count > 0)
            {
                array.EnsureCapacity(count);
                unsafe
                {
                    reader.BlitReadRawBytes(
                        "values",
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
                writer.Write("stream_guard", WorldStateStreamGuard);
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
            _world.GetNativeUniqueHeap().ClearAll(warnUndisposed: false);

            DeserializeImpl(reader);

            var guard = reader.Read<int>("stream_guard");
            Assert.IsEqual(guard, WorldStateStreamGuard);

            using (TrecsProfiling.Start("Triggering OnEcsDeserializeCompleted listeners"))
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

        void WriteSets(in NativeDenseDictionary<SetId, EntitySet> sets, ISerializationWriter writer)
        {
            var numSets = sets.Count;
            writer.Write("numSets", numSets);

            for (int i = 0; i < numSets; i++)
            {
                writer.Write("setId", sets.UnsafeKeys[i]);

                ref readonly var groupMap = ref sets.UnsafeValues[i]._entriesPerGroup;

                var numGroups = groupMap.Count;

                writer.Write("numGroups", numGroups);

                for (int k = 0; k < numGroups; k++)
                {
                    var group = groupMap.UnsafeKeys[k];
                    writer.Write("group", _worldDef.ToTagSet(group));

                    ref readonly var groupEntities = ref groupMap.UnsafeValues[k];

                    // This one looks ok to blit
                    writer.Write("entityIdToDenseIndex", groupEntities._entityIdToDenseIndex);
                }
            }
        }

        unsafe void WriteEntityHandlesMapInternal(
            ISerializationWriter writer,
            in NativeList<EntityHandleMapElement> entityIdMap
        )
        {
            writer.Write("count", entityIdMap.Length);

            writer.BlitWriteArrayPtr(
                "entityIdMap",
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

            writer.Write("count", count);

            for (int i = 0; i < count; i++)
            {
                var tagSet = _worldDef.ToTagSet(new GroupIndex((ushort)i));
                writer.Write("group", tagSet);

                var list = entityIndexToReferenceMap[i];
                writer.Write("listLength", list.Length);
                for (int j = 0; j < list.Length; j++)
                {
                    writer.Write("ref", list[j]);
                }
            }
        }

        void WriteEntityHandlesMap(ISerializationWriter writer)
        {
            ref var entityHandlesMap = ref _world.GetEntityQuerier()._entityLocator;

            WriteEntityHandlesMapInternal(writer, entityHandlesMap._entityHandleMap);
            WriteEntityIndexToReferenceMap(writer, entityHandlesMap._entityIndexToReferenceMap);

            int nextFreeIndex = entityHandlesMap._nextFreeIndex;
            writer.Write("nextFreeIndex", nextFreeIndex);
        }

        bool ShouldSkip(GroupIndex group, ComponentId componentId, ISerializationWriter writer)
        {
            // We do not want to serialize variable update components during checksum checks
            // because they will always desync
            // BUT we do need to serialize and deserialize them when reading/writing snapshots
            // because otherwise the component arrays are left in an invalid state
            //
            // Note: checksum data is only ever byte-compared, never deserialized, so
            // ReadGroupEntityComponentsDB does not need a corresponding ShouldSkip guard.
            // If that changes, the read path would need to skip the same components to
            // keep the bit stream aligned.

            if (!writer.HasFlag(SerializationFlags.IsForChecksum))
            {
                return false;
            }

            var componentType = TypeIdProvider.GetTypeFromId(componentId.Value);

            var template = _worldDef.GetResolvedTemplateForGroup(group);
            var componentDec = template.GetComponentDeclaration(componentType);

            return componentDec.VariableUpdateOnly;
        }

        void WriteGroupEntityComponentsDB(ISerializationWriter writer)
        {
            var groupEntityComponentsDB = _world.ComponentStore.GroupEntityComponentsDB;

            var numItems = groupEntityComponentsDB.Length;

            writer.Write("numItems", numItems);

            for (int i = 0; i < numItems; i++)
            {
                var bytesBefore = writer.NumBytesWritten;

                var group = new GroupIndex((ushort)i);
                writer.Write("group", _worldDef.ToTagSet(group));

                var subMap = groupEntityComponentsDB[i];

                var numComponents = subMap.Count;

                writer.Write("numComponents", numComponents);

                for (int k = 0; k < numComponents; k++)
                {
                    ComponentId componentId = subMap.UnsafeKeys[k].key;
                    writer.Write("componentId", componentId);

                    if (!ShouldSkip(group, componentId, writer))
                    {
                        IComponentArray unmanagedStrategy = subMap.UnsafeValues[k];
                        WriteComponentArray(unmanagedStrategy, writer);
                    }
                }

                _log.Trace(
                    "GroupIndex {} serialized in {} kb",
                    group,
                    (writer.NumBytesWritten - bytesBefore) / 1024f
                );
            }
        }

        void SerializeImpl(ISerializationWriter writer)
        {
            writer.Write("rngSeed", _world.FixedRng);
            writer.Write("fixedFrameCount", _world.SystemRunner._currentFixedFrameCount);
            writer.Write("fixedElapsedTime", _world.SystemRunner._elapsedFixedTime);

            var bytesBefore = writer.NumBytesWritten;

            using (TrecsProfiling.Start("Writing component arrays"))
            {
                WriteGroupEntityComponentsDB(writer);
            }

            _log.Trace(
                "Component arrays serialized in {} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );
            bytesBefore = writer.NumBytesWritten;

            using (TrecsProfiling.Start("Writing entity references map"))
            {
                WriteEntityHandlesMap(writer);
            }

            _log.Trace(
                "Entity references map serialized in {} kb",
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
                "Entity sets serialized in {} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );

            bytesBefore = writer.NumBytesWritten;

            using (TrecsProfiling.Start("Writing heap memory"))
            {
                var trecsWriter = new TrecsSerializationWriterAdapter(writer);
                _world.GetUniqueHeap().Serialize(trecsWriter);
                _world.GetSharedHeap().Serialize(trecsWriter);
                _world.GetNativeSharedHeap().Serialize(trecsWriter);
                _world.GetNativeUniqueHeap().Serialize(trecsWriter);
            }

            _log.Trace(
                "Heap memory serialized in {} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );
        }

        void WriteSetRoutingIndex(
            in NativeDenseDictionary<GroupIndex, NativeList<SetId>> routingIndex,
            ISerializationWriter writer
        )
        {
            writer.Write("numRoutingEntries", routingIndex.Count);

            for (int i = 0; i < routingIndex.Count; i++)
            {
                writer.Write("group", _worldDef.ToTagSet(routingIndex.UnsafeKeys[i]));
                writer.Write("filterIndices", routingIndex.UnsafeValues[i]);
            }
        }

        void ReadSetRoutingIndex(
            NativeDenseDictionary<GroupIndex, NativeList<SetId>> routingIndex,
            ISerializationReader reader
        )
        {
            var numEntries = reader.Read<int>("numRoutingEntries");

            // Clear existing routing entries
            foreach (var entry in routingIndex)
            {
                entry.Value.Dispose();
            }

            routingIndex.Clear();

            for (int i = 0; i < numEntries; i++)
            {
                var tagSet = reader.Read<TagSet>("group");
                var group = _worldDef.ToGroupIndex(tagSet);
                var setIndices = new NativeList<SetId>(1, Allocator.Persistent);
                reader.Read("filterIndices", ref setIndices);
                routingIndex.Add(group, setIndices);
            }
        }

        void ReadSets(NativeDenseDictionary<SetId, EntitySet> sets, ISerializationReader reader)
        {
            var numSets = reader.Read<int>("numSets");
            Assert.That(numSets >= 0);

            Assert.IsEqual(sets.Count, numSets);

            for (int i = 0; i < numSets; i++)
            {
                var setId = reader.Read<SetId>("setId");

                var currentSetId = sets.UnsafeKeys[i];
                Assert.IsEqual(setId, currentSetId);

                ref readonly var groupMap = ref sets.GetValueByRef(setId);

                var numGroups = reader.Read<int>("numGroups");
                groupMap.Clear();

                for (int k = 0; k < numGroups; k++)
                {
                    var tagSet = reader.Read<TagSet>("group");
                    var group = _worldDef.ToGroupIndex(tagSet);

                    var groupEntry = groupMap.GetSetGroupEntry(group);

                    // This one looks ok to blit
                    reader.Read("entityIdToDenseIndex", ref groupEntry._entityIdToDenseIndex);
                }
            }
        }

        unsafe void ReadEntityHandlesMapInternal(
            ISerializationReader reader,
            ref NativeList<EntityHandleMapElement> entityIdMap
        )
        {
            var size = reader.Read<int>("count");
            Assert.That(size >= 0);

            if (entityIdMap.Capacity < size)
            {
                entityIdMap.Capacity = size;
            }

            entityIdMap.ResizeUninitialized(size);

            reader.BlitReadArrayPtr(
                "entityIdMap",
                (EntityHandleMapElement*)entityIdMap.GetUnsafePtr(),
                size
            );
        }

        void ReadEntityIndexToReferenceMap(
            ISerializationReader reader,
            NativeList<UnsafeList<int>> entityIndexToReferenceMap
        )
        {
            var count = reader.Read<int>("count");
            Assert.That(count >= 0);

            Assert.IsEqual(count, entityIndexToReferenceMap.Length);

            for (int i = 0; i < count; i++)
            {
                var tagSet = reader.Read<TagSet>("group");
                var group = _worldDef.ToGroupIndex(tagSet);

                ref var groupList = ref entityIndexToReferenceMap.ElementAt(group.Index);

                var listLength = reader.Read<int>("listLength");
                groupList.Clear();
                groupList.Resize(listLength, NativeArrayOptions.ClearMemory);
                for (int j = 0; j < listLength; j++)
                {
                    groupList[j] = reader.Read<int>("ref");
                }
            }
        }

        void ReadEntityHandlesMap(ISerializationReader reader)
        {
            ref var entityHandlesMap = ref _world.GetEntityQuerier()._entityLocator;

            ReadEntityHandlesMapInternal(reader, ref entityHandlesMap._entityHandleMap);
            ReadEntityIndexToReferenceMap(reader, entityHandlesMap._entityIndexToReferenceMap);

            var nextFreeIndex = reader.Read<int>("nextFreeIndex");
            Assert.That(nextFreeIndex >= 0);

            entityHandlesMap._nextFreeIndex.Set(nextFreeIndex);
        }

        void ReadGroupEntityComponentsDB(ISerializationReader reader)
        {
            var groupEntityComponentsDB = _world.ComponentStore.GroupEntityComponentsDB;

            var numItems = reader.Read<int>("numItems");
            Assert.That(numItems >= 0);

            Assert.IsEqual(groupEntityComponentsDB.Length, numItems);

            for (int i = 0; i < numItems; i++)
            {
                var tagSet = reader.Read<TagSet>("group");
                var group = _worldDef.ToGroupIndex(tagSet);

                Assert.IsEqual(new GroupIndex((ushort)i), group);

                var subMap = groupEntityComponentsDB[i];

                var numComponents = reader.Read<int>("numComponents");

                Assert.That(
                    subMap.Count == numComponents,
                    "Unexpected number of components for group {}. Expected {}, got {}",
                    group,
                    subMap.Count,
                    numComponents
                );

                for (int k = 0; k < numComponents; k++)
                {
                    var componentId = reader.Read<ComponentId>("componentId");
                    Assert.IsEqual(subMap.UnsafeKeys[k].key, componentId);
                    ReadComponentArray(subMap.UnsafeValues[k], reader);
                }
            }
        }

        void DeserializeImpl(ISerializationReader reader)
        {
            reader.ReadInPlace("rngSeed", _world.FixedRng);
            _world.SystemRunner._currentFixedFrameCount = reader.Read<int>("fixedFrameCount");
            _world.SystemRunner._elapsedFixedTime = reader.Read<float>("fixedElapsedTime");

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
                var trecsReader = new TrecsSerializationReaderAdapter(reader);

                _world.GetUniqueHeap().Deserialize(trecsReader);
                _world.GetSharedHeap().Deserialize(trecsReader);
                _world.GetNativeSharedHeap().Deserialize(trecsReader);
                _world.GetNativeUniqueHeap().Deserialize(trecsReader);
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

    public interface IComponentArrayCustomSerializer
    {
        void Serialize(IComponentArray array, ISerializationWriter writer);
        void Deserialize(IComponentArray array, ISerializationReader reader);
    }
}
