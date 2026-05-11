using System;
using System.Collections.Generic;
using System.Linq;
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
    public sealed class WorldStateSerializer : IWorldStateSerializer
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

        /// <summary>
        /// Register a custom serializer for component type <typeparamref name="T"/>.
        /// If a serializer is already registered for the type the existing one is
        /// replaced and a warning is logged — callers that want strict
        /// duplicate-detection should call <see cref="TryGetCustomComponentSerializer{T}"/>
        /// first and decide what to do.
        /// </summary>
        public void RegisterCustomComponentSerializer<T>(IComponentArrayCustomSerializer serializer)
            where T : unmanaged, IEntityComponent
        {
            Assert.IsNotNull(serializer);
            var key = typeof(ComponentArray<T>);
            if (_customComponentSerializers.ContainsKey(key))
            {
                _log.Warning(
                    "Replacing existing custom component serializer for component type {}",
                    typeof(T)
                );
            }
            _customComponentSerializers[key] = serializer;
        }

        /// <summary>
        /// Remove the custom serializer registered for component type
        /// <typeparamref name="T"/>. Returns <c>true</c> if a serializer was
        /// removed, <c>false</c> if none was registered.
        /// </summary>
        public bool UnregisterCustomComponentSerializer<T>()
            where T : unmanaged, IEntityComponent
        {
            return _customComponentSerializers.Remove(typeof(ComponentArray<T>));
        }

        /// <summary>
        /// Retrieve the custom serializer registered for component type
        /// <typeparamref name="T"/>, if any. Returns <c>true</c> with the
        /// serializer in <paramref name="serializer"/> when registered,
        /// <c>false</c> with <c>null</c> when not.
        /// </summary>
        public bool TryGetCustomComponentSerializer<T>(
            out IComponentArrayCustomSerializer serializer
        )
            where T : unmanaged, IEntityComponent
        {
            return _customComponentSerializers.TryGetValue(
                typeof(ComponentArray<T>),
                out serializer
            );
        }

        /// <summary>
        /// Enumerate the component types that currently have a custom serializer
        /// registered. Returns the component value types (e.g. <c>typeof(CFoo)</c>),
        /// not the underlying <c>ComponentArray&lt;T&gt;</c> wrappers.
        /// </summary>
        public IEnumerable<Type> GetCustomComponentSerializerTypes()
        {
            return _customComponentSerializers.Keys.Select(k => k.GetGenericArguments()[0]);
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
            // Checksum streams skip VUO template component arrays via
            // ShouldSkip on the write side. They are not a valid input
            // for restore — the missing VUO entries would silently zero
            // out render-side state on load. Treat that as caller error
            // rather than corrupted data.
            Assert.That(
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

        void WriteSets(
            in NativeDenseDictionary<SetId, EntitySetStorage> sets,
            ISerializationWriter writer
        )
        {
            var numSets = sets.Count;
            writer.Write("numSets", numSets);

            for (int i = 0; i < numSets; i++)
            {
                writer.Write("setId", sets.UnsafeKeys[i]);

                ref readonly var set = ref sets.UnsafeValues[i];
                var registeredGroups = set._registeredGroups;
                var entriesPerGroup = set._entriesPerGroup;

                var numGroups = registeredGroups.Length;
                writer.Write("numGroups", numGroups);

                for (int k = 0; k < numGroups; k++)
                {
                    var group = registeredGroups[k];
                    writer.Write("group", _worldDef.ToTagSet(group));

                    var groupEntities = entriesPerGroup[group.Index];

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
                var tagSet = _worldDef.ToTagSet(GroupIndex.FromIndex(i));
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

            writer.Write("numItems", numItems);

            for (int i = 0; i < numItems; i++)
            {
                var bytesBefore = writer.NumBytesWritten;

                var group = GroupIndex.FromIndex(i);
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

            // Per-system deterministic paused state. Channel state (Editor /
            // Playback / User) is intentionally not serialized — those are
            // ephemeral, app-side concerns.
            using (TrecsProfiling.Start("Writing system enable state"))
            {
                var trecsWriter = new TrecsSerializationWriterAdapter(writer);
                _world.SystemEnableState.Serialize(trecsWriter);
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
            writer.Write("numRoutingEntries", nonEmptyCount);

            for (int i = 0; i < routingIndex.Length; i++)
            {
                var list = routingIndex[i];
                if (list.Length == 0)
                    continue;
                writer.Write("group", _worldDef.ToTagSet(GroupIndex.FromIndex(i)));
                writer.Write("listLength", list.Length);
                for (int j = 0; j < list.Length; j++)
                {
                    writer.Write("setId", list[j]);
                }
            }
        }

        void ReadSetRoutingIndex(
            NativeList<UnsafeList<SetId>> routingIndex,
            ISerializationReader reader
        )
        {
            var numEntries = reader.Read<int>("numRoutingEntries");

            // Clear existing contents of every slot (reset to empty).
            for (int i = 0; i < routingIndex.Length; i++)
            {
                routingIndex.ElementAt(i).Clear();
            }

            for (int i = 0; i < numEntries; i++)
            {
                var tagSet = reader.Read<TagSet>("group");
                var group = _worldDef.ToGroupIndex(tagSet);
                var listLength = reader.Read<int>("listLength");
                ref var list = ref routingIndex.ElementAt(group.Index);
                list.Resize(listLength, NativeArrayOptions.UninitializedMemory);
                for (int j = 0; j < listLength; j++)
                {
                    list[j] = reader.Read<SetId>("setId");
                }
            }
        }

        void ReadSets(
            NativeDenseDictionary<SetId, EntitySetStorage> sets,
            ISerializationReader reader
        )
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

                Assert.IsEqual(GroupIndex.FromIndex(i), group);

                var subMap = groupEntityComponentsDB[i];

                var numComponents = reader.Read<int>("numComponents");

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

            using (TrecsProfiling.Start("Reading system enable state"))
            {
                var trecsReader = new TrecsSerializationReaderAdapter(reader);
                _world.SystemEnableState.Deserialize(trecsReader);
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
