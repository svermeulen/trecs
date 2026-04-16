using System;
using System.Collections.Generic;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Serialization
{
    public class EcsDeserializeResult<TStaticSeed>
        where TStaticSeed : unmanaged
    {
        public EcsDeserializeResult(bool succeeded, TStaticSeed? requiredStaticSeed)
        {
            Succeeded = succeeded;
            RequiredStaticSeed = requiredStaticSeed;
        }

        public bool Succeeded { get; }
        public TStaticSeed? RequiredStaticSeed { get; }
    }

    public interface IComponentArrayCustomSerializer
    {
        void Serialize(IComponentArray array, ISerializationWriter writer);
        void Deserialize(IComponentArray array, ISerializationReader reader);
    }

    /// <summary>
    /// Optional class that can be used to serialize/deserialize the entire game state.
    /// Must be disposed after World.
    /// </summary>
    public class EcsStateSerializer
    {
        static readonly TrecsLog _log = new(nameof(EcsStateSerializer));

        readonly World _world;
        readonly WorldInfo _worldDef;
        readonly Dictionary<Type, IComponentArrayCustomSerializer> _customComponentSerializers =
            new();

        public EcsStateSerializer(World ecs)
        {
            _worldDef = ecs.WorldInfo;
            _world = ecs;

            RegisterComponentTypeIds(_worldDef);
        }

        static void RegisterComponentTypeIds(WorldInfo worldInfo)
        {
            var registered = new HashSet<Type>();

            foreach (var template in worldInfo.ResolvedTemplates)
            {
                foreach (var componentDec in template.ComponentDeclarations)
                {
                    var componentType = componentDec.ComponentType;

                    if (!registered.Add(componentType))
                    {
                        continue;
                    }

                    Assert.That(TypeIdProvider.IsRegistered(componentType));
                }
            }
        }

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

        public void SerializeStaticSeed<TStaticSeed>(
            in TStaticSeed staticSeed,
            ISerializationWriter writer
        )
            where TStaticSeed : unmanaged
        {
            using (TrecsProfiling.Start("Serializing static seed"))
            {
                // Note that we blit instead of write here
                // We do this so that other code can get static seed
                // easily without using serialization system
                // since it's sometimes needed before game load
                writer.BlitWrite("staticSeed", staticSeed);
                writer.BlitWrite("staticSeedSentinel", StaticSeedSentinel);
            }
        }

        public void SerializeState(ISerializationWriter writer)
        {
            using (TrecsProfiling.Start("Serializing game state"))
            {
                SerializeImpl(writer);
                writer.Write("sentinel", SentinelValue);
            }
        }

        public EcsDeserializeResult<TStaticSeed> DeserializeStaticSeed<TStaticSeed>(
            in TStaticSeed currentStaticSeed,
            ISerializationReader reader
        )
            where TStaticSeed : unmanaged
        {
            // Note that we Blit instead of Write here
            // We do this so that other code can get static seed
            // easily without using serialization system
            // since it's sometimes needed before game load
            TStaticSeed requiredStaticSeed = default;
            reader.BlitRead<TStaticSeed>("staticSeed", ref requiredStaticSeed);
            int staticSeedSentinel = 0;
            reader.BlitRead<int>("staticSeedSentinel", ref staticSeedSentinel);
            Assert.IsEqual(
                staticSeedSentinel,
                StaticSeedSentinel,
                "Sentinel values do not match in given data"
            );

            _log.Trace("Read static seed as: {@}", requiredStaticSeed);

            if (!requiredStaticSeed.Equals(currentStaticSeed))
            {
                return new EcsDeserializeResult<TStaticSeed>(
                    succeeded: false,
                    requiredStaticSeed: requiredStaticSeed
                );
            }

            return new EcsDeserializeResult<TStaticSeed>(succeeded: true, requiredStaticSeed: null);
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

            var sentinelValue = reader.Read<int>("sentinel");
            Assert.IsEqual(sentinelValue, SentinelValue);

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
                    writer.Write("group", group);

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
            in NativeDenseDictionary<Group, NativeList<EntityHandle>> entityIndexToReferenceMap
        )
        {
            var count = entityIndexToReferenceMap.Count;

            writer.Write("count", count);

            for (int i = 0; i < count; i++)
            {
                var group = entityIndexToReferenceMap.UnsafeKeys[i];
                writer.Write("group", group);

                var list = entityIndexToReferenceMap.UnsafeValues[i];
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

        bool ShouldSkip(Group group, ComponentId componentId, ISerializationWriter writer)
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

            if (!writer.HasFlag(TrecsSerializationFlags.IsForChecksum))
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

            var numItems = groupEntityComponentsDB.Count;

            writer.Write("numItems", numItems);

            for (int i = 0; i < numItems; i++)
            {
                var bytesBefore = writer.NumBytesWritten;

                Group group = groupEntityComponentsDB.UnsafeKeys[i].key;
                writer.Write("group", group);

                ref var subMap = ref groupEntityComponentsDB.UnsafeValues[i];

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
                    "Group {} serialized in {} kb",
                    group,
                    (writer.NumBytesWritten - bytesBefore) / 1024f
                );
            }
        }

        void WriteIdChecker(ISerializationWriter writer)
        {
            // _idChecker has been removed (indices are inherently unique).
            // Write disabled flag for backwards compatibility.
            writer.Write<bool>("IdCheckerEnabled", false);
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
            using (TrecsProfiling.Start("Writing ID checker"))
            {
                WriteIdChecker(writer);
            }
            _log.Trace(
                "ID checker serialized in {} kb",
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
            in NativeDenseDictionary<Group, NativeList<SetId>> routingIndex,
            ISerializationWriter writer
        )
        {
            writer.Write("numRoutingEntries", routingIndex.Count);

            for (int i = 0; i < routingIndex.Count; i++)
            {
                writer.Write("group", routingIndex.UnsafeKeys[i]);
                writer.Write("filterIndices", routingIndex.UnsafeValues[i]);
            }
        }

        void ReadSetRoutingIndex(
            NativeDenseDictionary<Group, NativeList<SetId>> routingIndex,
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
                var group = reader.Read<Group>("group");
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
                    var group = reader.Read<Group>("group");

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
            NativeDenseDictionary<Group, NativeList<EntityHandle>> entityIndexToReferenceMap
        )
        {
            var count = reader.Read<int>("count");
            Assert.That(count >= 0);

            Assert.IsEqual(count, entityIndexToReferenceMap.Count);

            for (int i = 0; i < count; i++)
            {
                var group = reader.Read<Group>("group");

                Assert.That(
                    entityIndexToReferenceMap.ContainsKey(group),
                    "Expected group {} to already be added as a key",
                    group
                );

                ref var groupList = ref entityIndexToReferenceMap.GetValueByRef(group);

                var listLength = reader.Read<int>("listLength");
                groupList.Clear();
                groupList.Resize(listLength, NativeArrayOptions.ClearMemory);
                for (int j = 0; j < listLength; j++)
                {
                    groupList[j] = reader.Read<EntityHandle>("ref");
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

            Assert.IsEqual(groupEntityComponentsDB.Count, numItems);

            for (int i = 0; i < numItems; i++)
            {
                var group = reader.Read<Group>("group");

                Assert.IsEqual(groupEntityComponentsDB.UnsafeKeys[i].key, group);

                ref var subMap = ref groupEntityComponentsDB.UnsafeValues[i];

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

        void ReadIdChecker(ISerializationReader reader)
        {
            // _idChecker has been removed (indices are inherently unique).
            // Read and discard the data for backwards compatibility.
            var idCheckerEnabled = reader.Read<bool>("IdCheckerEnabled");

            if (idCheckerEnabled)
            {
                var numItems = reader.Read<int>("numItems");

                for (int i = 0; i < numItems; i++)
                {
                    reader.Read<Group>("group");

                    NativeArray<uint> ids = default;
                    reader.Read("ids", ref ids);

                    if (ids.IsCreated)
                    {
                        ids.Dispose();
                    }
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

            ReadIdChecker(reader);
            using (TrecsProfiling.Start("Reading heap memory"))
            {
                var trecsReader = new TrecsSerializationReaderAdapter(reader);

                _world.GetUniqueHeap().Deserialize(trecsReader);
                _world.GetSharedHeap().Deserialize(trecsReader);
                _world.GetNativeSharedHeap().Deserialize(trecsReader);
                _world.GetNativeUniqueHeap().Deserialize(trecsReader);
            }
        }

        const int SentinelValue = 510120270;

        // Sometimes we need to read static seed manually outside of serialization system
        // so let's be paranoid and use a sentinel there
        public const int StaticSeedSentinel = 658447894;
    }
}
