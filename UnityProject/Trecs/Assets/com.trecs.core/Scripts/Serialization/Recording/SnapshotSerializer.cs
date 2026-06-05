using System;
using System.Collections.Generic;
using Trecs.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Captures and restores full ECS world-state snapshots as binary payloads. Main-thread only.
    /// Holds only immutable wiring (world refs, serialization helper) — no per-call data state:
    /// the byte target, metadata instance, and id sink are all supplied by the caller, with the
    /// reusable pieces grouped in a caller-owned <see cref="SnapshotSerializerScratch"/>.
    /// </summary>
    public class SnapshotSerializer
    {
        readonly SerializationHelper _helper;

        readonly WorldAccessor _world;

        // Kept alongside the accessor so PrepareMetadata can collect the snapshot's referenced blob
        // ids straight from the heaps (World.AddSerializedStateBlobIds) rather than from the cache's
        // global active set — see PrepareMetadata.
        readonly World _worldRoot;

        readonly BlobCache _blobCache;

        readonly WorldStateSerializer _worldStateSerializer;

        public SnapshotSerializer(
            World world,
            SerializerRegistry registry,
            WorldStateSerializer worldStateSerializer
        )
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));

            _helper = new SerializationHelper(registry);

            _world = world.CreateAccessor(AccessorRole.Unrestricted);
            _worldStateSerializer =
                worldStateSerializer
                ?? throw new ArgumentNullException(nameof(worldStateSerializer));

            _blobCache = world.GetBlobCache();
            _worldRoot = world;
        }

        /// <summary>
        /// Capture the current world state into <paramref name="target"/>, a (typically pooled)
        /// <see cref="SerializationData"/> the caller owns. The payload's two sections are written
        /// directly into <paramref name="target"/> (no contiguous copy); the caller then does
        /// whatever it wants with the result — retain it, checksum it
        /// (<see cref="SerializationData.ComputeContiguousChecksum"/>), or emit the contiguous
        /// form to a stream/file/buffer. Restore later with <see cref="Deserialize"/>.
        /// <para>
        /// Opaque (eager) blob bytes are <b>not</b> persisted here. The ids of every opaque blob
        /// the snapshot references are appended to <paramref name="opaqueBlobIdsOut"/> (the list
        /// is not cleared), and the caller decides how to keep them recoverable: write each one to
        /// a store via <see cref="OpaqueBlobPersistence.Persist"/> for a durable save, or pin it
        /// resident via <see cref="OpaqueBlobPersistence.Pin"/> for an in-memory snapshot. With
        /// <paramref name="requireOpaqueHandling"/> set, passing <c>null</c> asserts that the
        /// snapshot references no opaque blobs at all.
        /// </para>
        /// </summary>
        public void Serialize(
            int version,
            bool includeTypeChecks,
            SerializationData target,
            SnapshotSerializerScratch scratch,
            List<BlobId> opaqueBlobIdsOut,
            bool requireOpaqueHandling
        )
        {
            var metadata = scratch.Metadata;
            using (TrecsProfiling.Start("PrepareMetadata"))
            {
                PrepareMetadata(version, metadata);
            }

            try
            {
                var writer = _helper.Writer;
                using (TrecsProfiling.Start("SerializationWriter.Start"))
                {
                    writer.Start(target, version: version, includeTypeChecks: includeTypeChecks);
                }
                using (TrecsProfiling.Start("Writing snapshot metadata"))
                {
                    writer.Write("Metadata", metadata);
                }
                using (TrecsProfiling.Start("Writing opaque blob refs"))
                {
                    WriteOpaqueBlobs(writer, metadata, opaqueBlobIdsOut, requireOpaqueHandling);
                }
                // metadata.BlobIds was collected from the heaps by PrepareMetadata above, and
                // nothing between there and here mutates them — hand it down so the blob-journal
                // section reuses it instead of re-walking the heaps into an identical set (a
                // measurable cost per save at high blob counts, paid every rollback frame).
                _worldStateSerializer.SerializeFullState(
                    writer,
                    preCollectedBlobIds: metadata.BlobIds
                );
                using (TrecsProfiling.Start("SerializationWriter.Flush"))
                {
                    // No contiguous copy: the payload was written straight into target, which
                    // the caller holds and reads back (retain / checksum) after this returns.
                    writer.Complete();
                }
            }
            catch
            {
                _helper.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Compute the 64-bit xxHash checksum of the current world state. Serializes a full
        /// snapshot into the scratch's throwaway byte target (cleared by the writer on Start)
        /// and hashes the contiguous wire form in place — byte-identical to the capture-path
        /// checksum (<see cref="Serialize"/> + <see cref="SerializationData.ComputeContiguousChecksum"/>),
        /// so capture-time and verify-time checksums agree.
        /// </summary>
        public ulong ComputeChecksum(
            int version,
            bool includeTypeChecks,
            SnapshotSerializerScratch scratch
        )
        {
            // No opaque-id sink: a checksum is side-effect-free and discards the payload, so there
            // is nothing to persist or pin. The opaque-ref section is still emitted
            // (deterministic), so the checksum is stable.
            Serialize(
                version,
                includeTypeChecks,
                scratch.ChecksumData,
                scratch,
                opaqueBlobIdsOut: null,
                requireOpaqueHandling: false
            );
            return scratch.ChecksumData.ComputeContiguousChecksum();
        }

        /// <summary>
        /// Read just the snapshot metadata from a retained two-section <paramref name="data"/>
        /// without restoring the full world state or materializing a contiguous copy — the metadata
        /// sits in the first section, so only it is touched. Returns a freshly deserialized
        /// instance the caller may retain.
        /// </summary>
        public SnapshotMetadata PeekMetadata(IReadOnlySerializationData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            try
            {
                var reader = _helper.Reader;
                reader.Start(data);
                var metadata = reader.Read<SnapshotMetadata>("Metadata");
                reader.CompletePartial();
                return metadata;
            }
            catch
            {
                _helper.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Read just the opaque (eager) blob references from a snapshot payload, without restoring
        /// the full world state — the load-side pre-step for a snapshot whose blobs may no longer
        /// be resident (e.g. a fresh-process load from disk). Append each ref to
        /// <paramref name="refsOut"/> (the list is not cleared); make each one resident via
        /// <see cref="OpaqueBlobPersistence.Restore"/>, then call <see cref="Deserialize"/>.
        /// Snapshots loaded in-session (blobs pinned since capture) can skip this entirely.
        /// The refs sit just after the metadata section, so only that prefix is touched.
        /// </summary>
        public void PeekOpaqueBlobRefs(IReadOnlySerializationData data, List<OpaqueBlobRef> refsOut)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (refsOut == null)
            {
                throw new ArgumentNullException(nameof(refsOut));
            }
            try
            {
                var reader = _helper.Reader;
                reader.Start(data);
                reader.Read<SnapshotMetadata>("Metadata");
                var count = reader.Read<int>("Count");
                for (int i = 0; i < count; i++)
                {
                    var id = reader.Read<BlobId>("Id");
                    var typeId = reader.Read<TypeId>("TypeId");
                    bool isNative = reader.ReadBit();
                    refsOut.Add(new OpaqueBlobRef(id, typeId, isNative));
                }
                reader.CompletePartial();
            }
            catch
            {
                _helper.ResetForErrorRecovery();
                throw;
            }
        }

        void ValidateSchemaFingerprint(WorldSchemaFingerprint saved)
        {
            var current = _worldRoot.SchemaFingerprint;
            if (saved != current)
            {
                throw new SerializationException(
                    WorldSchemaFingerprintCalculator.BuildMismatchMessage(
                        "snapshot",
                        saved,
                        current
                    )
                );
            }
        }

        // Consume the OpaqueBlobs refs section and verify the load's precondition: every
        // referenced opaque (eager) blob is already resident in the cache — pinned since capture
        // (in-session reload), or restored beforehand by the caller via PeekOpaqueBlobRefs +
        // OpaqueBlobPersistence.Restore. The heaps re-pin each blob by id via CreateHandle during
        // DeserializeState, so residency must hold before then.
        void ReadOpaqueBlobs(ISerializationReader reader)
        {
            var count = reader.Read<int>("Count");

            for (int i = 0; i < count; i++)
            {
                var id = reader.Read<BlobId>("Id");
                reader.Read<TypeId>("TypeId");
                reader.ReadBit();

                TrecsAssert.That(
                    _blobCache.IsResident(id),
                    "Snapshot references opaque blob {0} whose bytes are not resident. Restore it "
                        + "before loading: PeekOpaqueBlobRefs + OpaqueBlobs.Restore with the store "
                        + "the snapshot was saved with.",
                    id
                );
            }
        }

        /// <summary>
        /// Restore the world state captured in <paramref name="data"/>, populating
        /// <paramref name="metadataOut"/> (overwriting whatever it held) with the snapshot's
        /// metadata section rather than allocating a new instance. Callers that don't retain the
        /// metadata pass their scratch's <see cref="SnapshotSerializerScratch.Metadata"/>.
        /// <para>
        /// Every opaque (eager) blob the snapshot references must already be resident in the
        /// <see cref="BlobCache"/> — pinned since capture, or restored from external storage
        /// beforehand via <see cref="PeekOpaqueBlobRefs"/> +
        /// <see cref="OpaqueBlobPersistence.Restore"/>. A non-resident reference throws.
        /// </para>
        /// </summary>
        public void Deserialize(IReadOnlySerializationData data, SnapshotMetadata metadataOut)
        {
            try
            {
                var reader = _helper.Reader;
                reader.Start(data);
                using (TrecsProfiling.Start("Reading snapshot metadata"))
                {
                    reader.ReadInPlace("Metadata", metadataOut);
                }

                // Gate before any world state is read: the world-state wire
                // format depends on the schema matching exactly, so a stale
                // snapshot must fail here — with an explanation — rather than
                // as a misaligned binary read deeper in the payload. One
                // struct compare; free on the rollback/replay hot path.
                ValidateSchemaFingerprint(metadataOut.SchemaFingerprint);

                // Opaque (eager) blobs whose bytes are not re-derivable must be resident before
                // DeserializeState — the heaps re-pin them by id via CreateHandle. ReadOpaqueBlobs
                // verifies that precondition. (The descriptor journal that re-registers derivable
                // blob sources travels inside the world-state stream's BlobJournal section instead.)
                using (TrecsProfiling.Start("Reading opaque blob refs"))
                {
                    ReadOpaqueBlobs(reader);
                }
                _worldStateSerializer.DeserializeState(reader);
                using (TrecsProfiling.Start("SerializationReader.Complete"))
                {
                    reader.Complete();
                }

                // The heaps' post-load blob membership came from this same snapshot that
                // populated metadataOut.BlobIds — membership and order both derive from the very
                // serialized structures the heaps restored (SharedHeap's _activeBlobs map,
                // NativeSharedHeap's dense lists), so BlobIds equals a heap collection taken at
                // the heaps-section boundary. Stamp it so the next save with this metadata
                // instance skips the rebuild — this is what keeps PrepareMetadata's skip alive
                // across the rollback loop's load-then-save cycle. The versions MUST be the ones
                // WorldStateSerializer captured at that boundary, not the heaps' current ones:
                // custom sections, OnEcsDeserializeCompleted, and DeserializeCompleted listeners
                // already ran by now and may have mutated membership — current-version stamping
                // would certify the wire set as fresh when it isn't, and the next save would
                // skip its rebuild and write metadata/journal sections that disagree with the
                // heaps. With boundary versions, any such mutation makes the stamp stale and the
                // next save rebuilds. (PeekMetadata deliberately does not stamp: it restores no
                // world state.)
                metadataOut.RuntimeBlobIdsStampWorld = _worldRoot;
                metadataOut.RuntimeBlobIdsSharedVersion =
                    _worldStateSerializer.LastHeapLoadSharedBlobVersion;
                metadataOut.RuntimeBlobIdsNativeVersion =
                    _worldStateSerializer.LastHeapLoadNativeBlobVersion;
            }
            catch
            {
                _helper.ResetForErrorRecovery();
                throw;
            }
        }

        void PrepareMetadata(int version, SnapshotMetadata metadata)
        {
            metadata.Version = version;
            metadata.FixedFrame = _world.FixedFrame;
            // Cached on the world — a struct copy here, nothing recomputed on
            // the per-frame recording hot path.
            metadata.SchemaFingerprint = _worldRoot.SchemaFingerprint;

            var sharedHeap = _worldRoot.GetSharedHeap();
            var nativeSharedHeap = _worldRoot.GetNativeSharedHeap();

            // Steady-state skip: when neither heap's blob membership changed since this metadata
            // instance was last stamped, BlobIds already holds exactly what the collection below
            // would rebuild (same membership, same insertion order — the collection is a
            // deterministic walk of unchanged heap state). In the rollback loop, where the same
            // scratch metadata saves (and checksums, and reloads) every frame over a stable blob
            // set, this skips the whole per-blob rebuild.
            if (
                metadata.RuntimeBlobIdsStampWorld == _worldRoot
                && metadata.RuntimeBlobIdsSharedVersion == sharedHeap.BlobMembershipVersion
                && metadata.RuntimeBlobIdsNativeVersion == nativeSharedHeap.BlobMembershipVersion
            )
            {
                // !TRECS_IS_PROFILING: the re-collect would put the cost the skip removes right
                // back into editor-backend bench numbers (same pattern as
                // WorldStateSerializer.RegisterComponentTypeIds).
#if DEBUG && !TRECS_IS_PROFILING
                DebugVerifyStampedBlobIds(metadata.BlobIds);
#endif
                return;
            }

            metadata.BlobIds.Clear();
            // Source the referenced-blob set from the heaps that hold the blobs, not from the
            // cache's global active set (GetAllActiveBlobIds). The latter also counts blobs pinned
            // by non-ECS holders — notably the rewind buffer's snapshot keyframes — which would leak
            // unrelated ids into this snapshot and, because the ref section is part of the hashed
            // wire form, make the same world state hash differently depending on how many keyframes
            // happen to be live. It also drops frame-scoped input-heap blobs that are not part of
            // serialized state. See World.AddSerializedStateBlobIds.
            _worldRoot.AddSerializedStateBlobIds(metadata.BlobIds);
            metadata.RuntimeBlobIdsStampWorld = _worldRoot;
            metadata.RuntimeBlobIdsSharedVersion = sharedHeap.BlobMembershipVersion;
            metadata.RuntimeBlobIdsNativeVersion = nativeSharedHeap.BlobMembershipVersion;
        }

#if DEBUG && !TRECS_IS_PROFILING
        // Scratch for DebugVerifyStampedBlobIds only — no data crosses calls.
        readonly IterableHashSet<BlobId> _debugStampVerifyBuffer = new();

        // Backstop for the membership-version trust chain: a heap mutation site that forgets to
        // bump its version, or external code mutating a stamped metadata's BlobIds, would
        // silently change the snapshot wire form. Re-collect and require exact equality —
        // membership AND insertion order, since the set is itself wire data and the journal
        // section iterates it.
        void DebugVerifyStampedBlobIds(IterableHashSet<BlobId> stamped)
        {
            _debugStampVerifyBuffer.Clear();
            _worldRoot.AddSerializedStateBlobIds(_debugStampVerifyBuffer);
            TrecsDebugAssert.That(
                _debugStampVerifyBuffer.Count == stamped.Count,
                "Stamped BlobIds is stale: holds {0} ids but the heaps reference {1}. A heap "
                    + "blob-membership mutation did not bump its BlobMembershipVersion, or "
                    + "BlobIds was mutated externally.",
                stamped.Count,
                _debugStampVerifyBuffer.Count
            );
            int expectedIndex = 0;
            foreach (var id in _debugStampVerifyBuffer)
            {
                TrecsDebugAssert.That(
                    stamped.TryGetIndex(id, out var actualIndex) && actualIndex == expectedIndex,
                    "Stamped BlobIds is stale: heap-referenced blob {0} missing or out of order "
                        + "(expected insertion index {1}). A heap blob-membership mutation did "
                        + "not bump its BlobMembershipVersion, or BlobIds was mutated externally.",
                    id,
                    expectedIndex
                );
                expectedIndex++;
            }
        }
#endif

        // ── Opaque (eager) blob persistence ─────────────────────────────────
        //
        // Opaque blobs (BlobCache.Alloc*) have no descriptor or factory to re-derive their bytes,
        // so — unlike descriptor-interned blobs, which travel as a tiny journal entry inside the
        // world-state stream — their bytes must be persisted externally to survive a fresh-process
        // load. For each *active* eager blob we record (id, typeId, isNative) in the stream and
        // report the id to the caller via opaqueBlobIdsOut; the caller then persists
        // (OpaqueBlobs.Persist) or pins (OpaqueBlobs.Pin) each one. The refs are always emitted
        // (so the stream stays self-describing and checksum-stable) even when the caller supplies
        // no sink — a checksum pass doesn't.
        void WriteOpaqueBlobs(
            ISerializationWriter writer,
            SnapshotMetadata metadata,
            List<BlobId> opaqueBlobIdsOut,
            bool requireOpaqueHandling
        )
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            writer.PushScope("OpaqueBlobs");

            // O(1) common-case skip: when the cache holds no eager blob at all, no referenced id
            // can be eager either (every referenced blob is pinned by a heap handle, and pinned ⇒
            // resident), so the section is an empty list — without paying the two per-referenced-id
            // metadata-probe passes below on every rollback-frame save. Wire form is identical to
            // the eagerCount == 0 outcome of the full path.
            if (!_blobCache.HasEagerResidentBlobs)
            {
                // !TRECS_IS_PROFILING: keeps the probe cost the skip removes out of
                // editor-backend bench numbers.
#if DEBUG && !TRECS_IS_PROFILING
                foreach (var id in metadata.BlobIds)
                {
                    TrecsDebugAssert.That(
                        !_blobCache.GetBlobMetadata(id).IsEager,
                        "HasEagerResidentBlobs is false but referenced blob {0} is eager — the "
                            + "eager-resident counter has desynced from the resident store",
                        id
                    );
                }
#endif
                writer.Write("Count", 0);
                writer.PopScope();
                return;
            }

            // metadata.BlobIds was just populated by PrepareMetadata with the active-blob set, in
            // the same deterministic order the rest of the stream relies on. First pass counts the
            // eager subset (the count is written before the entries); the second pass writes them —
            // no retained scratch list.
            int eagerCount = 0;
            foreach (var id in metadata.BlobIds)
            {
                if (_blobCache.GetBlobMetadata(id).IsEager)
                {
                    eagerCount++;
                }
            }

            // A durable save that references eager blobs but reports their ids to no one would
            // emit dangling refs — the snapshot loads back as "references opaque blob X whose
            // bytes are not resident" and throws. Catch it here, at save time, with the count in
            // hand. The checksum path passes requireOpaqueHandling: false: it discards the
            // payload, so there is legitimately nothing to persist.
            TrecsAssert.That(
                !requireOpaqueHandling || eagerCount == 0 || opaqueBlobIdsOut != null,
                "Saving a snapshot that references {0} opaque (eager) blob(s) without an "
                    + "opaqueBlobIdsOut list to report them in — the snapshot would not be "
                    + "loadable once a referenced blob is evicted. Pass a list and persist or "
                    + "pin each reported id.",
                eagerCount
            );

            writer.Write("Count", eagerCount);
            foreach (var id in metadata.BlobIds)
            {
                var blobMetadata = _blobCache.GetBlobMetadata(id);
                if (!blobMetadata.IsEager)
                {
                    continue;
                }
                writer.Write("Id", id);
                writer.Write("TypeId", blobMetadata.TypeId);
                writer.WriteBit(blobMetadata.IsNative);
                opaqueBlobIdsOut?.Add(id);
            }

            writer.PopScope();
        }
    }
}
