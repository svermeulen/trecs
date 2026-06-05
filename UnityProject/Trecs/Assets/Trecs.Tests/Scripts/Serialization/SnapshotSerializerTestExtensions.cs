using System;
using System.Collections.Generic;
using System.IO;
using Trecs.Internal;

namespace Trecs.Tests
{
    /// <summary>
    /// Test-only conveniences over the stateless <see cref="SnapshotSerializer"/>, mirroring the
    /// save/load sinks the old facade used to expose (stream / file / contiguous bytes / checksum)
    /// before the API was reduced to the scratch-taking primitives and blob persistence moved to
    /// the caller (via <see cref="OpaqueBlobPersistence"/>). Production callers own a
    /// <see cref="SnapshotSerializerScratch"/> and project a <see cref="SerializationData"/> to
    /// their chosen sink; tests keep these thin wrappers for readability. Main-thread only (the
    /// shared scratch assumes Unity's sequential test runner). A null
    /// <c>opaqueBlobStore</c> save asserts that the snapshot references no opaque blobs
    /// (matching production save paths with no store wired up); passing a store also requires the
    /// <see cref="World"/> so the wrapper can build the <see cref="OpaqueBlobPersistence"/> the
    /// production caller would hold.
    /// </summary>
    static class SnapshotSerializerTestExtensions
    {
        // Shared across all tests — Unity's test runner is sequential and main-thread, the same
        // constraint production scratch owners live under.
        static readonly SnapshotSerializerScratch _scratch = new();

        // ── Facade-equivalent primitives ────────────────────────────────────

        public static void SaveSnapshot(
            this SnapshotSerializer serializer,
            int version,
            SerializationData target,
            bool includeTypeChecks,
            List<BlobId> opaqueBlobIdsOut = null
        )
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            serializer.Serialize(
                version,
                includeTypeChecks,
                target,
                _scratch,
                opaqueBlobIdsOut,
                requireOpaqueHandling: true
            );
        }

        public static ulong ComputeChecksum(
            this SnapshotSerializer serializer,
            int version,
            bool includeTypeChecks
        )
        {
            return serializer.ComputeChecksum(version, includeTypeChecks, _scratch);
        }

        public static SnapshotMetadata LoadSnapshot(
            this SnapshotSerializer serializer,
            IReadOnlySerializationData data
        )
        {
            var metadata = new SnapshotMetadata();
            serializer.Deserialize(data, metadata);
            return metadata;
        }

        public static SnapshotMetadata LoadSnapshot(
            this SnapshotSerializer serializer,
            Stream stream
        )
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            return serializer.LoadSnapshot(_scratch.ReadBuffer.Load(stream));
        }

        public static SnapshotMetadata LoadSnapshot(
            this SnapshotSerializer serializer,
            ReadOnlyMemory<byte> payload
        )
        {
            if (payload.IsEmpty)
            {
                throw new SerializationException(
                    "Snapshot payload is empty — cannot load an empty snapshot."
                );
            }
            return serializer.LoadSnapshot(_scratch.ReadBuffer.Wrap(payload));
        }

        public static SnapshotMetadata LoadSnapshot(
            this SnapshotSerializer serializer,
            string filePath
        )
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Snapshot file not found", filePath);
            return serializer.LoadSnapshot(new ReadOnlyMemory<byte>(File.ReadAllBytes(filePath)));
        }

        public static SnapshotMetadata PeekMetadata(
            this SnapshotSerializer serializer,
            Stream stream
        )
        {
            return serializer.PeekMetadata(_scratch.ReadBuffer.Load(stream));
        }

        public static SnapshotMetadata PeekMetadata(
            this SnapshotSerializer serializer,
            ReadOnlyMemory<byte> payload
        )
        {
            return serializer.PeekMetadata(_scratch.ReadBuffer.Wrap(payload));
        }

        public static SnapshotMetadata PeekMetadata(
            this SnapshotSerializer serializer,
            string filePath
        )
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Snapshot file not found", filePath);
            return serializer.PeekMetadata(new ReadOnlyMemory<byte>(File.ReadAllBytes(filePath)));
        }

        public static void PeekOpaqueBlobRefs(
            this SnapshotSerializer serializer,
            ReadOnlyMemory<byte> payload,
            List<OpaqueBlobRef> refsOut
        )
        {
            serializer.PeekOpaqueBlobRefs(_scratch.ReadBuffer.Wrap(payload), refsOut);
        }

        // ── Blob-store round-trip helpers ───────────────────────────────────

        public static SnapshotMetadata SaveSnapshotToStream(
            this SnapshotSerializer serializer,
            int version,
            Stream stream,
            bool includeTypeChecks = false,
            IOpaqueBlobStore opaqueBlobStore = null,
            World world = null
        )
        {
            var data = SaveWithBlobs(
                serializer,
                version,
                includeTypeChecks,
                opaqueBlobStore,
                world
            );
            data.WriteContiguousTo(stream);
            return serializer.PeekMetadata(data);
        }

        public static SnapshotMetadata SaveSnapshotToFile(
            this SnapshotSerializer serializer,
            int version,
            string filePath,
            bool includeTypeChecks = false,
            IOpaqueBlobStore opaqueBlobStore = null,
            World world = null
        )
        {
            var data = SaveWithBlobs(
                serializer,
                version,
                includeTypeChecks,
                opaqueBlobStore,
                world
            );
            using var fs = File.Create(filePath);
            data.WriteContiguousTo(fs);
            return serializer.PeekMetadata(data);
        }

        /// <summary>
        /// Restore opaque blobs from <paramref name="opaqueBlobStore"/> and load the snapshot —
        /// the two-step production load flow (peek refs → restore → load) as one call.
        /// </summary>
        public static SnapshotMetadata LoadSnapshot(
            this SnapshotSerializer serializer,
            Stream stream,
            IOpaqueBlobStore opaqueBlobStore,
            World world
        )
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var payload = new ReadOnlyMemory<byte>(ms.GetBuffer(), 0, (int)ms.Length);
            return serializer.LoadSnapshot(payload, opaqueBlobStore, world);
        }

        /// <inheritdoc cref="LoadSnapshot(SnapshotSerializer, Stream, IOpaqueBlobStore, World)"/>
        public static SnapshotMetadata LoadSnapshot(
            this SnapshotSerializer serializer,
            ReadOnlyMemory<byte> payload,
            IOpaqueBlobStore opaqueBlobStore,
            World world
        )
        {
            world
                .CreateOpaqueBlobs()
                .RestoreReferencedBlobs(
                    serializer,
                    _scratch.ReadBuffer.Wrap(payload),
                    opaqueBlobStore,
                    new List<OpaqueBlobRef>()
                );
            return serializer.LoadSnapshot(payload);
        }

        /// <inheritdoc cref="LoadSnapshot(SnapshotSerializer, Stream, IOpaqueBlobStore, World)"/>
        public static SnapshotMetadata LoadSnapshot(
            this SnapshotSerializer serializer,
            IReadOnlySerializationData data,
            IOpaqueBlobStore opaqueBlobStore,
            World world
        )
        {
            world
                .CreateOpaqueBlobs()
                .RestoreReferencedBlobs(
                    serializer,
                    data,
                    opaqueBlobStore,
                    new List<OpaqueBlobRef>()
                );
            return serializer.LoadSnapshot(data);
        }

        /// <summary>
        /// The <see cref="OpaqueBlobPersistence"/> a production caller of
        /// <see cref="SnapshotSerializer"/> would construct and hold alongside it.
        /// </summary>
        public static OpaqueBlobPersistence CreateOpaqueBlobs(this World world)
        {
            return new OpaqueBlobPersistence(world.SerializerRegistry, world.GetBlobCache());
        }

        static SerializationData SaveWithBlobs(
            SnapshotSerializer serializer,
            int version,
            bool includeTypeChecks,
            IOpaqueBlobStore opaqueBlobStore,
            World world
        )
        {
            var data = new SerializationData();
            var opaqueBlobIds = opaqueBlobStore != null ? new List<BlobId>() : null;
            serializer.SaveSnapshot(version, data, includeTypeChecks, opaqueBlobIds);
            if (opaqueBlobIds != null)
            {
                var opaqueBlobs = world.CreateOpaqueBlobs();
                foreach (var id in opaqueBlobIds)
                {
                    opaqueBlobs.Persist(id, opaqueBlobStore);
                }
            }
            return data;
        }
    }
}
