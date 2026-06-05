using System;
using System.Collections.Generic;

namespace Trecs.Internal
{
    /// <summary>
    /// Moves opaque (eager) blob bytes between the <see cref="BlobCache"/> and external storage.
    /// Opaque blobs (<c>BlobCache.Alloc*</c>) have no descriptor or factory to re-derive their
    /// bytes, so a snapshot that references one only records its id — keeping the bytes
    /// recoverable is the caller's job, and this class is the toolbox for it:
    /// <list type="bullet">
    /// <item><see cref="Persist"/> — write a resident blob's bytes to a content-addressed
    /// <see cref="IOpaqueBlobStore"/> (durable saves).</item>
    /// <item><see cref="Pin"/> — keep a blob resident without any disk round-trip (in-memory
    /// snapshots, e.g. the live editor recorder).</item>
    /// <item><see cref="Restore"/> — re-insert a non-resident blob from a store before loading a
    /// snapshot that references it.</item>
    /// </list>
    /// <see cref="SnapshotSerializer"/> reports the ids/refs to feed these methods (save:
    /// <c>opaqueBlobIdsOut</c>; load: <see cref="SnapshotSerializer.PeekOpaqueBlobRefs(IReadOnlySerializationData, System.Collections.Generic.List{OpaqueBlobRef})"/>)
    /// but performs no byte persistence itself. Owns its own serialization scratch (via
    /// <see cref="OpaqueBlobBaker"/>); reused across calls, main-thread only.
    /// </summary>
    public sealed class OpaqueBlobPersistence
    {
        readonly BlobCache _blobCache;

        // Bakes/restores the blob bytes themselves, owning the serialization scratch.
        readonly OpaqueBlobBaker _baker;

        internal OpaqueBlobPersistence(SerializerRegistry registry, BlobCache blobCache)
        {
            _baker = new OpaqueBlobBaker(registry);
            _blobCache = blobCache;
        }

        /// <summary>
        /// Write the bytes of a currently-resident opaque (eager) blob to <paramref name="store"/>,
        /// skipping if the content-addressed id is already present. A blob that is neither in the
        /// store nor resident is a no-op — that can only be a loaded-recording blob already
        /// persisted in the shared store.
        /// </summary>
        public void Persist(BlobId id, IOpaqueBlobStore store)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (store.Contains(id) || !_blobCache.IsResident(id))
            {
                return;
            }
            store.Write(
                id,
                stream =>
                    _baker.SerializeResidentBlob(
                        _blobCache,
                        id,
                        stream,
                        OpaqueBlobBaker.CurrentFormatVersion
                    )
            );
        }

        /// <summary>
        /// Pin a currently-resident opaque (eager) blob in the <see cref="BlobCache"/> so it
        /// survives eviction while an in-memory snapshot still references it — the live editor
        /// recorder's alternative to writing the bytes through to a store at capture time. Call
        /// with the ids <see cref="SnapshotSerializer.Serialize"/> just reported, while they
        /// are still resident. The caller owns the returned pin and must dispose its handle when
        /// the snapshot is dropped; flush the bytes to a store only when persisting to a file
        /// (see <see cref="Persist"/>).
        /// </summary>
        internal BlobPin Pin(BlobId id)
        {
            return new BlobPin(id, _blobCache.CreateHandle(id));
        }

        /// <summary>
        /// Restore every opaque (eager) blob referenced by <paramref name="data"/> that is not
        /// already resident — the mandatory pre-step before
        /// <see cref="SnapshotSerializer.Deserialize"/> when loading a snapshot whose blobs may
        /// have left the cache (fresh process, eviction). <paramref name="refsScratch"/> is
        /// cleared and reused; pass a caller-owned list to keep repeated loads alloc-free.
        /// </summary>
        public void RestoreReferencedBlobs(
            SnapshotSerializer serializer,
            IReadOnlySerializationData data,
            IOpaqueBlobStore store,
            List<OpaqueBlobRef> refsScratch
        )
        {
            refsScratch.Clear();
            serializer.PeekOpaqueBlobRefs(data, refsScratch);
            foreach (var blobRef in refsScratch)
            {
                Restore(blobRef, store);
            }
        }

        /// <summary>
        /// Restore one opaque (eager) blob from <paramref name="store"/> into the
        /// <see cref="BlobCache"/>, so a snapshot referencing it can be loaded. Already-resident
        /// ids are a no-op (content-addressed: a resident blob under the same id has the same
        /// bytes). The blob is seeded directly as an eager blob — no lazy source needed (a fresh
        /// restore re-supplies it if it's later evicted); the heaps then re-pin it via
        /// <c>CreateHandle</c> during the snapshot load, so it ends up exactly like a runtime
        /// Alloc'd blob. This is the load-side mirror of <see cref="Persist"/>.
        /// </summary>
        public void Restore(in OpaqueBlobRef blobRef, IOpaqueBlobStore store)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (_blobCache.IsResident(blobRef.Id))
            {
                return;
            }

            TrecsAssert.That(
                store.TryOpenRead(blobRef.Id, out var blobStream),
                "IOpaqueBlobStore has no bytes for opaque blob {0}.",
                blobRef.Id
            );
            TrecsAssert.That(
                TypeId.TryToType(blobRef.TypeId, out var blobType),
                "Could not resolve type id {0} for opaque blob {1}; the type must be registered "
                    + "before restoring it.",
                blobRef.TypeId,
                blobRef.Id
            );

            object blob;
            using (blobStream)
            {
                blob = _baker.Deserialize(
                    blobStream,
                    blobType,
                    blobRef.IsNative,
                    OpaqueBlobBaker.CurrentFormatVersion,
                    _blobCache.NativeBlobBoxPool
                );
            }
            _blobCache.InsertEagerBlob(blobRef.Id, blob);
        }
    }
}
