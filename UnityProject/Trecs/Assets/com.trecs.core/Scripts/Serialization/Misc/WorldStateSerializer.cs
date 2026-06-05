using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Serializes/deserializes the entire game state of a <see cref="World"/>.
    /// Orchestration only: this class owns section ordering, section guards,
    /// and the deserialize lifecycle events. The wire format of each section
    /// lives with the subsystem that owns the data (e.g.
    /// <see cref="Internal.ComponentStore"/>, <see cref="EntityHandleMap"/>,
    /// <see cref="Internal.SetStore"/>, the heaps).
    /// To override how a particular component type's array is serialized
    /// (e.g. to skip transient state or reset a runtime handle on load),
    /// register an <see cref="IComponentArraySerializer{T}"/> via
    /// <see cref="World.ComponentArraySerializerRegistry"/>.
    /// To include deterministic game state that lives outside the ECS (e.g.
    /// a scripting VM's state), register an
    /// <see cref="ICustomWorldStateSection"/> via
    /// <see cref="World.CustomWorldStateSections"/> — it is appended after
    /// the built-in sections on every serialize/deserialize.
    /// </summary>
    public sealed class WorldStateSerializer
    {
        readonly TrecsLog _log;

        readonly World _world;
        readonly WorldInfo _worldDef;

        // Reusable buffers for the active-blob descriptor journal section, so the recording hot
        // path doesn't allocate a set + dictionary per snapshot.
        readonly IterableHashSet<BlobId> _activeBlobIdsBuffer = new();
        readonly IterableDictionary<BlobId, object> _blobJournalBuffer = new();

        // The two shared heaps' BlobMembershipVersions captured immediately after the heaps
        // section of the most recent deserialize — the moment heap blob membership provably
        // equals the stream's blob set. SnapshotSerializer stamps its metadata's BlobIds from
        // these (not from versions read after DeserializeState returns) because custom sections,
        // OnEcsDeserializeCompleted, and DeserializeCompleted listeners all run after the heaps
        // load and may legitimately mutate blob membership; stamping with post-listener versions
        // would mark the wire set as current when it no longer is, and the next save would skip
        // its rebuild and emit metadata/journal sections that disagree with the heaps. With the
        // capture taken at the heaps boundary, any later mutation bumps the live version past
        // the captured one and the stamp simply fails to match — falling back to a rebuild.
        internal long LastHeapLoadSharedBlobVersion { get; private set; }
        internal long LastHeapLoadNativeBlobVersion { get; private set; }

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

        /// <summary>
        /// Serializes the full world state. <paramref name="preCollectedBlobIds"/> optionally
        /// supplies the state-referenced blob set (<c>World.AddSerializedStateBlobIds</c> into a
        /// cleared buffer, with no world mutation since) so the blob-journal section can reuse it
        /// instead of re-walking the heaps — the snapshot path collects the same set moments
        /// earlier for its metadata, and rebuilding it costs a heap walk plus one hash-set insert
        /// per referenced blob on the per-frame rollback save. Pass null to collect internally.
        /// </summary>
        public void SerializeFullState(
            ISerializationWriter writer,
            IterableHashSet<BlobId> preCollectedBlobIds = null
        )
        {
            using (TrecsProfiling.Start("Serializing game state"))
            {
                SerializeImpl(writer, preCollectedBlobIds);
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
            // NativeSharedHeap is deliberately NOT cleared here: its Deserialize
            // reconciles the incoming entries against the live ones (keeping the
            // blob-cache handle for every unchanged slot — the hot rollback path),
            // which requires the pre-load state to survive until it runs.
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

        /// <summary>
        /// Restores the full game state from <paramref name="reader"/>. There is no
        /// rollback on failure: deserialization wipes the heaps before repopulating
        /// them, so if this throws (e.g. <see cref="SerializationException"/> on a
        /// corrupt or incompatible stream) the world is left half-restored and must
        /// be discarded — do not catch the exception and keep ticking the world.
        /// </summary>
        public void DeserializeState(ISerializationReader reader)
        {
            using (TrecsProfiling.Start("State Deserialization"))
            {
                DeserializeStateImpl(reader);
            }
        }

        // The descriptor journal for the blobs this state references: for each state-referenced blob
        // that was interned from a descriptor, persist (id, descriptor) so a fresh-process load can
        // re-register the blob's source and re-derive it before the heaps pin it by id. The
        // referenced set comes from the heaps that hold the blobs (AddSerializedStateBlobIds), NOT
        // from BlobCache's global active set: heap-derived ids are limited to serialized state and
        // their order is deterministic (so the wire form is stable for checksums), whereas the cache
        // active set also counts non-ECS pins (e.g. rewind-buffer keyframes) whose presence and
        // ordering are not a deterministic function of world state. Written/read before the heaps.
        void WriteBlobJournal(
            ISerializationWriter writer,
            IterableHashSet<BlobId> preCollectedBlobIds
        )
        {
            var activeIds = preCollectedBlobIds;
            if (activeIds == null)
            {
                _activeBlobIdsBuffer.Clear();
                _world.AddSerializedStateBlobIds(_activeBlobIdsBuffer);
                activeIds = _activeBlobIdsBuffer;
            }
#if DEBUG && !TRECS_IS_PROFILING
            else
            {
                // The caller vouched that its set matches what a fresh collection would produce
                // (same heaps, no mutation since). The journal's wire form iterates this set, so
                // a stale or reordered one wouldn't just be slow — it would change the bytes.
                // Verify membership AND insertion order in debug. (!TRECS_IS_PROFILING: the
                // re-collect would put the cost the reuse removes right back into editor-backend
                // bench numbers.)
                _activeBlobIdsBuffer.Clear();
                _world.AddSerializedStateBlobIds(_activeBlobIdsBuffer);
                TrecsDebugAssert.That(
                    _activeBlobIdsBuffer.Count == activeIds.Count,
                    "preCollectedBlobIds is stale: {0} ids passed but the heaps reference {1}",
                    activeIds.Count,
                    _activeBlobIdsBuffer.Count
                );
                int expectedIndex = 0;
                foreach (var id in _activeBlobIdsBuffer)
                {
                    TrecsDebugAssert.That(
                        activeIds.TryGetIndex(id, out var actualIndex)
                            && actualIndex == expectedIndex,
                        "preCollectedBlobIds is stale: heap-referenced blob {0} missing or out "
                            + "of order (expected insertion index {1})",
                        id,
                        expectedIndex
                    );
                    expectedIndex++;
                }
            }
#endif
            _blobJournalBuffer.Clear();
            _world.BlobFactory.CollectJournaledDescriptors(activeIds, _blobJournalBuffer);

            writer.Write("Count", _blobJournalBuffer.Count);
            foreach (var (id, descriptor) in _blobJournalBuffer)
            {
                writer.Write("Id", id);
                writer.WriteObject("Descriptor", descriptor);
            }
        }

        void ReadBlobJournal(ISerializationReader reader)
        {
            _blobJournalBuffer.Clear();
            var count = reader.Read<int>("Count");
            for (int i = 0; i < count; i++)
            {
                var id = reader.Read<BlobId>("Id");
                object descriptor = null;
                reader.ReadObject("Descriptor", ref descriptor);
                _blobJournalBuffer.Add(id, descriptor);
            }
            _world.BlobFactory.RestoreJournaledDescriptors(_blobJournalBuffer);
        }

        void SerializeImpl(ISerializationWriter writer, IterableHashSet<BlobId> preCollectedBlobIds)
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
                _world.ComponentStore.Serialize(
                    writer,
                    _worldDef,
                    _world.ComponentArraySerializerRegistry
                );
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
                _world.GetEntityQuerier()._entityLocator.Serialize(writer, _worldDef);
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
                _world.GetSetStore().Serialize(writer, _worldDef);
            }
            writer.PopScope();
            WriteSectionGuard(WorldStateSection.AfterEntitySets, writer);
            _log.Trace(
                "Entity sets serialized in {0} kb",
                (writer.NumBytesWritten - bytesBefore) / 1024f
            );

            writer.PushScope("BlobJournal");
            using (TrecsProfiling.Start("Writing blob journal"))
            {
                WriteBlobJournal(writer, preCollectedBlobIds);
            }
            writer.PopScope();
            WriteSectionGuard(WorldStateSection.AfterBlobJournal, writer);

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

            writer.PushScope("CustomSections");
            using (TrecsProfiling.Start("Writing custom sections"))
            {
                WriteCustomSections(writer);
            }
            writer.PopScope();
            WriteSectionGuard(WorldStateSection.AfterCustomSections, writer);

            writer.PopScope();
        }

        // Game-defined sections (World.CustomWorldStateSections), appended
        // after the built-in sections in registration order. Each entry is
        // framed by its name hash (so a registration mismatch is reported by
        // name) and a per-entry guard (so wire drift inside one section is
        // pinned to that section instead of cascading into the next).
        void WriteCustomSections(ISerializationWriter writer)
        {
            var sections = _world.CustomWorldStateSections;
            writer.Write("Count", sections.Count);
            for (int i = 0; i < sections.Count; i++)
            {
                var entry = sections.GetEntry(i);
                writer.BlitWrite("NameHash", entry.NameHash);
                writer.PushScope(entry.Name);
                entry.Section.Serialize(writer);
                writer.PopScope();
                WriteSectionGuard(WorldStateSection.AfterCustomSectionEntry, writer);
            }
        }

        void ReadCustomSections(ISerializationReader reader)
        {
            var sections = _world.CustomWorldStateSections;
            var count = reader.Read<int>("Count");
            if (count != sections.Count)
            {
                // SerializationException rather than an assert for the same
                // reason as the section guards: a section-set mismatch in a
                // release build must fail loudly here, not cascade as a
                // misaligned binary read. The schema fingerprint normally
                // rejects such payloads earlier, but raw world-state streams
                // (no snapshot metadata) skip that gate.
                throw new SerializationException(
                    $"World-state stream contains {count} custom section(s) but the world has "
                        + $"{sections.Count} registered. The set of registered "
                        + "ICustomWorldStateSections must match the one the stream was "
                        + "written with."
                );
            }

            for (int i = 0; i < count; i++)
            {
                var entry = sections.GetEntry(i);
                ulong nameHash = 0;
                reader.BlitRead("NameHash", ref nameHash);
                if (nameHash != entry.NameHash)
                {
                    throw new SerializationException(
                        $"Custom world-state section #{i} mismatch: the stream was written "
                            + $"with a different section than the registered '{entry.Name}'. "
                            + "Registration names and order must match the stream."
                    );
                }
                entry.Section.Deserialize(reader);

                // Inline guard check (not ReadSectionGuard) so the error names
                // the specific custom section that drifted — with several
                // registered sections, "AfterCustomSectionEntry" alone doesn't
                // say whose Serialize/Deserialize pair is out of sync.
                byte guard = 0;
                reader.BlitRead("SectionGuard", ref guard);
                if (guard != (byte)WorldStateSection.AfterCustomSectionEntry)
                {
                    throw new SerializationException(
                        $"Custom world-state section '{entry.Name}' stream drift: expected "
                            + $"its trailing guard byte "
                            + $"0x{(byte)WorldStateSection.AfterCustomSectionEntry:X2} but got "
                            + $"0x{guard:X2}. The section's Serialize and Deserialize do not "
                            + "consume mirrored wire data."
                    );
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
                _world.ComponentStore.Deserialize(
                    reader,
                    _worldDef,
                    _world.ComponentArraySerializerRegistry
                );
            }
            ReadSectionGuard(WorldStateSection.AfterComponentArrays, reader);

            using (TrecsProfiling.Start("Reading entity references map"))
            {
                _world.GetEntityQuerier()._entityLocator.Deserialize(reader, _worldDef);
            }
            ReadSectionGuard(WorldStateSection.AfterEntityHandles, reader);

            using (TrecsProfiling.Start("Reading entity sets"))
            {
                _world.GetSetStore().Deserialize(reader, _worldDef);
            }
            ReadSectionGuard(WorldStateSection.AfterEntitySets, reader);

            using (TrecsProfiling.Start("Reading blob journal"))
            {
                ReadBlobJournal(reader);
            }
            ReadSectionGuard(WorldStateSection.AfterBlobJournal, reader);

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

            // Capture here — see the property docs. Heap blob membership equals the stream's
            // blob set exactly at this boundary; later sections and listeners may change it.
            LastHeapLoadSharedBlobVersion = _world.GetSharedHeap().BlobMembershipVersion;
            LastHeapLoadNativeBlobVersion = _world.GetNativeSharedHeap().BlobMembershipVersion;

            using (TrecsProfiling.Start("Reading system enable state"))
            {
                _world.SystemEnableState.Deserialize(reader);
            }
            ReadSectionGuard(WorldStateSection.AfterSystemEnable, reader);

            using (TrecsProfiling.Start("Reading custom sections"))
            {
                ReadCustomSections(reader);
            }
            ReadSectionGuard(WorldStateSection.AfterCustomSections, reader);
        }

        // Section markers written between major blocks of the world-state
        // stream. Drift between a subsystem's Serialize and its Deserialize
        // counterpart is caught at the section boundary instead of cascading
        // as garbage into the next block, and the assert message pinpoints
        // which section diverged. One byte each — total cost is a handful of
        // bytes per snapshot. Values are chosen out of the natural range of
        // small ints/lengths that surround them, so they're obvious in a hex
        // dump.
        enum WorldStateSection : byte
        {
            AfterTimingFields = 0xA1,
            AfterComponentArrays = 0xA2,
            AfterEntityHandles = 0xA3,
            AfterEntitySets = 0xA4,
            AfterBlobJournal = 0xA7,
            AfterHeaps = 0xA5,
            AfterSystemEnable = 0xA6,

            // Written after each registered ICustomWorldStateSection's
            // payload, and once after the whole custom-sections block.
            AfterCustomSectionEntry = 0xA8,
            AfterCustomSections = 0xA9,
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
                        + "A section's wire format does not match its "
                        + "deserialize counterpart."
                );
            }
        }
    }
}
