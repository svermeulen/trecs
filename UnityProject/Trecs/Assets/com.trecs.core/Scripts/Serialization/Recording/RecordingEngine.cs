using System;
using System.Collections.Generic;
using Trecs.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Stateful collaborator shared by <see cref="BundleRecorder"/> /
    /// <see cref="BundleReplayer"/> (runtime) and <see cref="TrecsRewindBuffer"/>
    /// (editor) to consolidate the lifecycle and IO primitives every owner in
    /// the recording stack needs: an <see cref="WorldAccessor"/> with
    /// <see cref="AccessorRole.Unrestricted"/>, reusable input-queue
    /// serialization buffers, <see cref="RecordingBundle"/> assembly
    /// (blob-root seeding + header population), the
    /// <see cref="OnFixedUpdateCompleted"/> subscription helper, and the
    /// input-history-locker add/remove pair.
    ///
    /// Intentionally narrow: capture/playback *cadence* and *policy* (when to
    /// capture a keyframe, when to checksum, whether to cap, what
    /// <see cref="IInputHistoryLocker.MaxClearFrame"/> to report, how to react
    /// to a desync) stay in the owner. The owners differ on those
    /// (BundleRecorder has one keyframe cadence and no capacity cap;
    /// TrecsRewindBuffer has a second scrub-cache cadence plus drop-oldest
    /// caps; BundleReplayer is a playback state machine) and folding them in
    /// here would mean callbacks-and-options. The lifecycle/IO bits don't
    /// differ and that's all this collaborator owns.
    ///
    /// Main-thread only; not thread-safe. One engine per owner.
    /// </summary>
    internal sealed class RecordingEngine
    {
        readonly World _world;
        readonly TrecsLog _log;
        readonly SerializerRegistry _serializerRegistry;
        readonly SnapshotSerializer _snapshotSerializer;

        // Held for owners that persist/pin/restore opaque snapshot blobs
        // (TrecsRewindBuffer); the engine itself never touches it. Null for
        // owners whose snapshots must not reference opaque blobs
        // (BundleRecorder, BundleReplayer).
        readonly OpaqueBlobPersistence _opaqueBlobPersistence;

        readonly WorldAccessor _accessor;

        // Caller-owned working space for the stateless SnapshotSerializer; reused across
        // calls. Main-thread only, like the rest of this type.
        readonly SnapshotSerializerScratch _snapshotScratch = new();

        // Reused across queue-serialization calls so the write/read buffers survive the recorder's
        // lifetime — successive recording sessions on the same recorder have roughly stable sizes,
        // so this avoids re-growing the byte[] every time.
        readonly SerializationHelper _queueHelper;
        readonly SerializationData _queueData = new(); // write scratch for SerializeEntityInputQueue
        readonly SerializationReadBuffer _queueReadBuffer = new(); // read view for DeserializeEntityInputQueue

        // Optional content-addressed store for opaque (eager) input-blob bytes, plus a baker that
        // owns the scratch for (de)serializing them. Null store = opaque input blobs are not
        // persisted (as before).
        readonly IOpaqueBlobStore _opaqueBlobStore;
        readonly OpaqueBlobBaker _opaqueBlobBaker;

        // Refs scratch for LoadSnapshotRestoringBlobs; reused across loads.
        readonly List<OpaqueBlobRef> _opaqueRefScratch = new();

        public RecordingEngine(
            World world,
            SerializerRegistry serializerRegistry,
            SnapshotSerializer snapshotSerializer,
            string accessorLabel,
            OpaqueBlobPersistence opaqueBlobPersistence = null,
            IOpaqueBlobStore opaqueBlobStore = null
        )
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (serializerRegistry == null)
                throw new ArgumentNullException(nameof(serializerRegistry));
            if (snapshotSerializer == null)
                throw new ArgumentNullException(nameof(snapshotSerializer));
            if (accessorLabel == null)
                throw new ArgumentNullException(nameof(accessorLabel));

            _world = world;
            _log = world.Log;
            _serializerRegistry = serializerRegistry;
            _snapshotSerializer = snapshotSerializer;
            _opaqueBlobPersistence = opaqueBlobPersistence;
            _accessor = _world.CreateAccessor(AccessorRole.Unrestricted, accessorLabel);
            _queueHelper = new SerializationHelper(_serializerRegistry);
            _opaqueBlobStore = opaqueBlobStore;
            _opaqueBlobBaker = new OpaqueBlobBaker(_serializerRegistry);
        }

        public World World => _world;
        public WorldAccessor Accessor => _accessor;
        public SnapshotSerializer SnapshotSerializer => _snapshotSerializer;

        /// <summary>Null unless the owner passed one at construction.</summary>
        public OpaqueBlobPersistence OpaqueBlobPersistence => _opaqueBlobPersistence;

        /// <summary>
        /// Compute the current world-state checksum. Serializes a full
        /// snapshot into the internal buffer and hashes the result,
        /// consistent with the checksum a caller would compute from a
        /// <see cref="SaveSnapshot"/> result via
        /// <see cref="SerializationData.ComputeContiguousChecksum"/>.
        /// </summary>
        public ulong ComputeChecksum(int version)
        {
            using var _profile = TrecsProfiling.Start("SnapshotSerializer.ComputeChecksum");

            return _snapshotSerializer.ComputeChecksum(
                version,
                includeTypeChecks: true,
                _snapshotScratch
            );
        }

        /// <summary>
        /// Capture the current world state into <paramref name="target"/>. The ids of every
        /// opaque (eager) blob the snapshot references are appended to
        /// <paramref name="opaqueBlobIdsOut"/> for the caller to persist or pin; passing
        /// <c>null</c> asserts the snapshot references none.
        /// </summary>
        public void SaveSnapshot(
            int version,
            SerializationData target,
            List<BlobId> opaqueBlobIdsOut = null
        )
        {
            using var _profile = TrecsProfiling.Start("SnapshotSerializer.SaveSnapshot");

            _snapshotSerializer.Serialize(
                version,
                includeTypeChecks: true,
                target,
                _snapshotScratch,
                opaqueBlobIdsOut,
                requireOpaqueHandling: true
            );

            _log.Trace("Saved snapshot ({0:0.00} kb)", target.ContiguousSize / 1024f);
        }

        /// <summary>
        /// Restore world state from a retained two-section snapshot (live capture path). The
        /// snapshot's metadata is read into the reused scratch — no per-load allocation.
        /// </summary>
        public void LoadSnapshot(IReadOnlySerializationData data)
        {
            using var _profile = TrecsProfiling.Start("SnapshotSerializer.LoadSnapshot");

            _snapshotSerializer.Deserialize(data, _snapshotScratch.Metadata);
        }

        /// <summary>
        /// Restore world state from a contiguous snapshot payload (loaded-bundle path). Read in
        /// place through the reused view — no contiguous copy, no per-load metadata allocation.
        /// </summary>
        public void LoadSnapshot(ReadOnlyMemory<byte> payload)
        {
            using var _profile = TrecsProfiling.Start("SnapshotSerializer.LoadSnapshot");

            _snapshotSerializer.Deserialize(
                _snapshotScratch.ReadBuffer.Wrap(payload),
                _snapshotScratch.Metadata
            );
        }

        /// <summary>
        /// Restore world state from a contiguous snapshot payload whose opaque (eager) blobs may
        /// be non-resident (loaded-from-disk path), restoring them from the engine's store first.
        /// Requires the engine to have been constructed with both an
        /// <see cref="Trecs.Internal.OpaqueBlobPersistence"/> and an <see cref="IOpaqueBlobStore"/>.
        /// </summary>
        public void LoadSnapshotRestoringBlobs(ReadOnlyMemory<byte> payload)
        {
            TrecsAssert.That(
                _opaqueBlobPersistence != null,
                "LoadSnapshotRestoringBlobs requires an OpaqueBlobPersistence at engine construction."
            );
            TrecsAssert.That(
                _opaqueBlobStore != null,
                "LoadSnapshotRestoringBlobs requires an IOpaqueBlobStore at engine construction."
            );
            _opaqueBlobPersistence.RestoreReferencedBlobs(
                _snapshotSerializer,
                _snapshotScratch.ReadBuffer.Wrap(payload),
                _opaqueBlobStore,
                _opaqueRefScratch
            );
            LoadSnapshot(payload);
        }

        /// <summary>
        /// Read just the opaque (eager) blob references from a contiguous snapshot payload —
        /// the load-side pre-step for restoring non-resident blobs from a store.
        /// </summary>
        public void PeekOpaqueBlobRefs(ReadOnlyMemory<byte> payload, List<OpaqueBlobRef> refsOut)
        {
            _snapshotSerializer.PeekOpaqueBlobRefs(
                _snapshotScratch.ReadBuffer.Wrap(payload),
                refsOut
            );
        }

        /// <summary>
        /// Read just the snapshot metadata from a contiguous snapshot payload, without restoring
        /// world state. Returns a freshly deserialized instance the caller may retain.
        /// </summary>
        public SnapshotMetadata PeekMetadata(ReadOnlyMemory<byte> payload)
        {
            return _snapshotSerializer.PeekMetadata(_snapshotScratch.ReadBuffer.Wrap(payload));
        }

        /// <summary>
        /// Serialize the world's <c>EntityInputQueue</c> into a fresh
        /// <c>byte[]</c>. The reused queue buffer is cleared first so a shorter
        /// queue this call than the previous one doesn't carry stale trailing
        /// bytes into <c>ToArray</c>. On exception the buffer is forced back
        /// to Idle so subsequent calls can safely reuse it.
        /// </summary>
        public byte[] SerializeEntityInputQueue(int version)
        {
            try
            {
                var writer = _queueHelper.Writer;
                writer.Start(_queueData, version: version, includeTypeChecks: true);
                _accessor
                    .GetEntityInputQueue()
                    .Serialize(writer, _opaqueBlobStore, _opaqueBlobBaker);
                writer.Complete();

                var bytes = new byte[_queueData.ContiguousSize];
                _queueData.CopyContiguousTo(bytes);
                return bytes;
            }
            catch
            {
                _queueHelper.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Deserialize bundled input-queue bytes back into the world's
        /// <c>EntityInputQueue</c>. Counterpart to
        /// <see cref="SerializeEntityInputQueue"/>, used by the editor
        /// recorder's load-from-file path. On exception the buffer is
        /// forced back to Idle.
        /// </summary>
        public void DeserializeEntityInputQueue(ReadOnlyMemory<byte> bytes)
        {
            try
            {
                var reader = _queueHelper.Reader;
                reader.Start(_queueReadBuffer.Wrap(bytes));
                _accessor
                    .GetEntityInputQueue()
                    .Deserialize(reader, _opaqueBlobStore, _opaqueBlobBaker);
                reader.Complete();
            }
            catch
            {
                _queueHelper.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Seed a recording's blob root set from the heaps that semantically
        /// own references — the sim heaps (live world state) and the input
        /// heaps (the retained input window) — NOT <c>GetAllActiveBlobIds</c>,
        /// whose global active set would also sweep unrelated ambient pins
        /// (rendering anchors, seeder warm-ups) into the saved header:
        /// bloating the blob store, rooting junk in its GC set, and making
        /// the header's contents vary with whatever ambient code pinned at
        /// save time. Owners union additional roots in afterwards when their
        /// retained snapshots reference blobs the live world has since
        /// dropped (see <c>TrecsRecordingFile.Save</c>).
        /// </summary>
        public IterableHashSet<BlobId> CreateBundleBlobRootSet()
        {
            var blobs = new IterableHashSet<BlobId>();
            _world.AddSerializedStateBlobIds(blobs);
            _world.AddInputStreamBlobIds(blobs);
            return blobs;
        }

        /// <summary>
        /// Serialize the live <c>EntityInputQueue</c> and wrap a recording's
        /// parts into a <see cref="RecordingBundle"/> with a fully-populated
        /// header. Shared assembly tail of <c>BundleRecorder.Stop</c> and
        /// <c>TrecsRecordingFile.Save</c> (the rewind buffer's save path) —
        /// cadence and policy (which snapshots go in, what the end frame
        /// means) stay with the owners; the wire-format mechanics (header
        /// field population, queue envelope) live here so the two paths
        /// can't drift.
        ///
        /// The returned bundle's <c>Header.BlobIds</c> is the
        /// <paramref name="blobRoots"/> instance — owners may union more ids
        /// into it after assembly (mutating the header's set in place) before
        /// persisting the bundle.
        /// </summary>
        public RecordingBundle AssembleBundle(
            int version,
            int startFixedFrame,
            int endFixedFrame,
            IterableHashSet<BlobId> blobRoots,
            ReadOnlyMemory<byte> initialSnapshot,
            IReadOnlyList<WorldSnapshot> keyframes,
            IReadOnlyList<WorldSnapshot> bookmarks,
            IterableDictionary<int, ulong> checksums
        )
        {
            var queueBytes = SerializeEntityInputQueue(version);
            return new RecordingBundle
            {
                Header = new BundleHeader
                {
                    Version = version,
                    StartFixedFrame = startFixedFrame,
                    EndFixedFrame = endFixedFrame,
                    FixedDeltaTime = _accessor.GetSystemRunner().FixedDeltaTime,
                    BlobIds = blobRoots,
                    SchemaFingerprint = _world.SchemaFingerprint,
                },
                InitialSnapshot = initialSnapshot,
                InputQueue = queueBytes,
                Checksums = checksums,
                Keyframes = keyframes,
                Bookmarks = bookmarks,
            };
        }

        /// <summary>
        /// Force the queue buffer back to Idle. Called from recover paths
        /// where a step *outside* the try block in
        /// <see cref="SerializeEntityInputQueue"/> /
        /// <see cref="DeserializeEntityInputQueue"/> faults and a subsequent
        /// use of the buffer would otherwise assert.
        /// </summary>
        public void ResetQueueBufferForErrorRecovery()
        {
            _queueHelper.ResetForErrorRecovery();
        }

        /// <summary>
        /// Subscribe to the world's <c>OnFixedUpdateCompleted</c> event via
        /// the cached accessor. Returns the subscription token; callers
        /// dispose it to detach.
        /// </summary>
        public IDisposable SubscribeFixedUpdateCompleted(Action handler)
        {
            return _accessor.Events.OnFixedUpdateCompleted(handler);
        }

        /// <summary>
        /// Add <paramref name="locker"/> to the world's
        /// <c>EntityInputQueue</c> as a history locker. No-op if the world
        /// has been disposed.
        /// </summary>
        public void AddHistoryLocker(IInputHistoryLocker locker)
        {
            if (_world.IsDisposed)
                return;
            _accessor.GetEntityInputQueue().AddHistoryLocker(locker);
        }

        /// <summary>
        /// Remove <paramref name="locker"/> from the world's
        /// <c>EntityInputQueue</c>. No-op if the world has been disposed.
        /// </summary>
        public void RemoveHistoryLocker(IInputHistoryLocker locker)
        {
            if (_world.IsDisposed)
                return;
            _accessor.GetEntityInputQueue().RemoveHistoryLocker(locker);
        }
    }
}
