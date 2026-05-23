using System;
using System.Collections.Concurrent;
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
        /// <summary>
        /// Serialize the world's full state for a snapshot or recording —
        /// round-trip-capable bytes that <see cref="DeserializeState"/> can
        /// restore from. The writer must NOT have
        /// <see cref="SerializationFlags.IsForChecksum"/> set (that flag
        /// strips VUO template arrays and is incompatible with restore).
        /// </summary>
        void SerializeFullState(ISerializationWriter writer);

        /// <summary>
        /// Serialize a deterministic-only view of the world's state for
        /// xxHash desync detection. VUO template arrays and other
        /// non-deterministic fields are stripped via the
        /// <see cref="SerializationFlags.IsForChecksum"/> flag. The bytes
        /// are one-way — there is no symmetric Deserialize because the
        /// missing fields would corrupt the live world.
        /// </summary>
        void SerializeForChecksum(ISerializationWriter writer);

        /// <summary>
        /// Restore world state from bytes produced by
        /// <see cref="SerializeFullState"/>. Reader's
        /// <see cref="SerializationFlags.IsForChecksum"/> flag must be
        /// unset.
        /// </summary>
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
            TrecsDebugAssert.That(
                !writer.HasFlag(SerializationFlags.IsForChecksum),
                "SerializeFullState requires the writer NOT to have SerializationFlags.IsForChecksum. Use SerializeForChecksum if you want the stripped checksum payload."
            );
            using (TrecsProfiling.Start("Serializing game state"))
            {
                SerializeImpl(writer);
            }
        }

        public void SerializeForChecksum(ISerializationWriter writer)
        {
            TrecsDebugAssert.That(
                writer.HasFlag(SerializationFlags.IsForChecksum),
                "SerializeForChecksum requires the writer to be started with flags: SerializationFlags.IsForChecksum so app-side serializers can branch on the flag and strip non-deterministic fields."
            );
            using (TrecsProfiling.Start("Computing world state checksum"))
            {
                SerializeImpl(writer);
            }
        }

        void DeserializeStateImpl(ISerializationReader reader)
        {
            // Checksum streams strip VUO template component arrays via
            // ShouldSkip on the write side. The interface's Serialize/
            // Deserialize split makes the checksum-then-deserialize misuse
            // structurally impossible, but the flag still drives custom
            // IComponentArraySerializer dispatchers, so cross-check that the
            // reader's flag is consistent with the round-trip expectation.
            TrecsDebugAssert.That(
                !reader.HasFlag(SerializationFlags.IsForChecksum),
                "DeserializeState requires the reader NOT to have SerializationFlags.IsForChecksum — checksum streams are one-way."
            );

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
            // NativeChunkStore.Deserialize is sufficient to reset their state
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

                    writer.Write("EntityIdToDenseIndex", groupEntities._entityIdToDenseIndex);
                }
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
                var tagSet = _worldDef.ToTagSet(GroupIndex.FromIndex(i));
                writer.Write("Group", tagSet);

                writer.Write("Refs", entityIndexToReferenceMap[i]);
            }
        }

        void WriteEntityHandlesMap(ISerializationWriter writer)
        {
            ref var entityHandlesMap = ref _world.GetEntityQuerier()._entityLocator;

            writer.Write("EntityIdMap", in entityHandlesMap._entityHandleMap);
            WriteEntityIndexToReferenceMap(entityHandlesMap._entityIndexToReferenceMap, writer);

            int nextFreeIndex = entityHandlesMap._nextFreeIndex;
            writer.Write("NextFreeIndex", nextFreeIndex);
        }

        // Per-componentType cache for ChecksumIgnoreAttribute lookups —
        // reflection in a frame-rate-sensitive checksum loop would dominate
        // its own cost. ConcurrentDictionary because WorldStateSerializer is
        // shared but the lookups themselves are read-mostly and lock-free in
        // the common case.
        readonly ConcurrentDictionary<Type, bool> _hasChecksumIgnore = new();

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
        bool ShouldSkip(GroupIndex group, TypeId componentId, ISerializationWriter writer)
        {
            if (!writer.HasFlag(SerializationFlags.IsForChecksum))
            {
                return false;
            }

            var componentType = TypeId.ToType(new TypeId(componentId.Value));

            // (a) User opt-out via [ChecksumIgnore].
            if (HasChecksumIgnore(componentType))
            {
                return true;
            }

            // (b) Framework decision: variable-update-only component arrays
            // are skipped from checksums since their contents would otherwise
            // always desync between runs.
            var template = _worldDef.GetResolvedTemplateForGroup(group);
            var componentDec = template.GetComponentDeclaration(componentType);
            return template.IsVariableUpdateOnly(componentDec);
        }

        bool HasChecksumIgnore(Type componentType)
        {
            return _hasChecksumIgnore.GetOrAdd(
                componentType,
                static t => t.IsDefined(typeof(ChecksumIgnoreAttribute), inherit: false)
            );
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

                // Component-array slots are materialized lazily on first
                // entity. A group can end up with populated slots but zero
                // entities (e.g. after deserialization preallocates the
                // schema). For checksum serialization this materialization
                // state is a non-observable implementation detail — treat
                // an all-empty group identically to a never-materialized
                // one so checksums stay stable across recording/playback.
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

                writer.Write("NumComponents", numComponents);

                for (int k = 0; k < numComponents; k++)
                {
                    TypeId componentId = subMap.UnsafeKeys[k].Key;
                    writer.Write("TypeId", componentId);

                    if (!ShouldSkip(group, componentId, writer))
                    {
                        IComponentArray componentArray = subMap.UnsafeValues[k];
                        WriteComponentArray(componentArray, writer);
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
            WriteSectionGuard(WorldStateSection.AfterTimingFields, writer);

            var bytesBefore = writer.NumBytesWritten;

            using (TrecsProfiling.Start("Writing component arrays"))
            {
                WriteGroupEntityComponentsDB(writer);
            }
            WriteSectionGuard(WorldStateSection.AfterComponentArrays, writer);

            _log.Trace(
                "Component arrays serialized in {0} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );
            bytesBefore = writer.NumBytesWritten;

            using (TrecsProfiling.Start("Writing entity references map"))
            {
                WriteEntityHandlesMap(writer);
            }
            WriteSectionGuard(WorldStateSection.AfterEntityHandles, writer);

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
            WriteSectionGuard(WorldStateSection.AfterEntitySets, writer);
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
                // Chunk store bulk-dumps pages + side-table state. Persistent
                // NativeUniquePtr / TrecsList allocations live entirely in the chunk
                // store, so their slots round-trip through this dump and re-resolve
                // naturally on read (per-slot TypeIds tell each collection which slots
                // it owns). Input-allocated unmanaged data (InputNativeUniquePtr) is
                // owned by InputNativeUniqueHeap and serializes separately as part
                // of EntityInputQueue.
                _world.GetNativeUniqueChunkStore().Serialize(writer);
            }
            WriteSectionGuard(WorldStateSection.AfterHeaps, writer);

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
            WriteSectionGuard(WorldStateSection.AfterSystemEnable, writer);
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
                writer.Write("SetIds", in list);
            }
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

        void ReadSets(
            NativeDenseDictionary<SetId, EntitySetStorage> sets,
            ISerializationReader reader
        )
        {
            var numSets = reader.Read<int>("NumSets");
            TrecsDebugAssert.That(numSets >= 0);

            TrecsDebugAssert.IsEqual(sets.Count, numSets);

            for (int i = 0; i < numSets; i++)
            {
                var setId = reader.Read<SetId>("SetId");

                var currentSetId = sets.UnsafeKeys[i];
                TrecsDebugAssert.IsEqual(setId, currentSetId);

                ref readonly var groupMap = ref sets.GetValueByRef(setId);

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

                ReadSets(setStore.EntitySets, reader);
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
