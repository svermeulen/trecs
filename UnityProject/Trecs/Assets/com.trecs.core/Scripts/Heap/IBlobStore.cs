using System;

namespace Trecs.Internal
{
    /// <summary>
    /// Framework-internal contract for blob storage backends (e.g. disk, asset bundles,
    /// in-memory). One or more blob stores are composed into a <see cref="BlobCache"/>;
    /// the first writable store receives new blobs.
    /// <para>
    /// <b>Not intended for external implementation.</b> Use the supplied stores
    /// (<see cref="BlobStoreInMemory"/>, and the Svkj-package <c>BlobStoreFiles</c> /
    /// <c>BlobStoreAddressables</c>). The interface is exposed publicly only so callers
    /// can register concrete instances via <see cref="WorldBuilder.AddBlobStore"/>; its
    /// shape and members may change between minor versions without notice.
    /// </para>
    /// </summary>
    public interface IBlobStore : IDisposable
    {
        bool TryGetManifestEntry(BlobId id, out BlobMetadata manifestEntry, bool updateAccessTime);

        int SerializationVersion { set; }

        /// <summary>
        /// Logger shared by all framework classes for this world. <see cref="BlobCache"/>
        /// pushes its own logger here at construction time so the store can emit messages
        /// that respect the world's <see cref="WorldSettings.MinLogLevel"/>.
        /// </summary>
        TrecsLog Log { set; }

        /// <summary>
        /// Pool used to rent <see cref="NativeBlobBox"/> wrappers when reading native blobs
        /// off the underlying medium. <see cref="BlobCache"/> pushes the world-scoped pool
        /// here at construction time. Stores that never deserialize native blobs (e.g. the
        /// in-memory store) may ignore the setter.
        /// </summary>
        NativeBlobBoxPool NativeBlobBoxPool { set; }

        void CleanCache(ReadOnlyBlobIdSet activeBlobs);

        /// <summary>
        /// Configured cap on inactive native blob bytes for this store, in bytes
        /// (i.e. the byte equivalent of a settings <c>MaxInactiveNativeBlobsMb</c>).
        /// <see cref="BlobCache"/> aggregates this across stores at construction
        /// time to derive the inline-eviction high-water mark.
        /// </summary>
        long MaxInactiveNativeBytes { get; }

        /// <summary>
        /// Configured cap on inactive managed (class) blob count for this store.
        /// <see cref="BlobCache"/> aggregates this across stores at construction
        /// time to derive the inline-eviction high-water mark.
        /// </summary>
        int MaxInactiveManagedCount { get; }

        /// <summary>
        /// Walk this store's in-memory cache and add the contribution of every
        /// <i>inactive</i> entry (not present in <paramref name="activeBlobs"/>) to
        /// the running totals. Used by <see cref="BlobCache"/> to re-anchor its
        /// running estimators to truth after a clean pass — the estimators are
        /// updated incrementally on the hot path and only fully reconciled here.
        /// </summary>
        void SumInMemoryInactiveTotals(
            ReadOnlyBlobIdSet activeBlobs,
            ref long nativeBytes,
            ref int managedCount
        );

        /// <summary>
        /// Snapshot of this store's in-memory cache occupancy. Walks the cache
        /// once and reports total / inactive native bytes and total / inactive
        /// managed entry count. Aggregated by <see cref="BlobCache.GetStats"/>
        /// and surfaced per-store by <see cref="BlobCache.GetStatsPerStore"/>.
        /// </summary>
        BlobStoreStats GetStats(ReadOnlyBlobIdSet activeBlobs);

        void CreateBlobImpl(BlobId id, object blob, bool isNative);

        void ForcePurgeBlob(BlobId id);

        /// <summary>
        /// Returns the blob bytes and metadata for <paramref name="id"/>.
        /// <para><b>Loading contract.</b> Stores that may not have the blob bytes
        /// resident (e.g. disk-backed, asset-bundle) must, on a manifest hit, ensure
        /// the bytes are loaded before returning <c>true</c> — synchronously
        /// blocking if necessary. Async callers are expected to call
        /// <see cref="WarmUpBlob"/> first and poll <see cref="GetBlobLoadingState"/>
        /// until it reports <see cref="BlobLoadingState.Loaded"/>; a
        /// <c>TryGetBlobAndMetadata</c> call against a not-yet-loaded blob is a
        /// signal that the caller skipped that dance and is willing to block.</para>
        /// </summary>
        bool TryGetBlobAndMetadata(
            BlobId id,
            out object blob,
            out BlobMetadata metadata,
            bool updateAccessTime
        );

        /// <summary>
        /// Returns true if this store knows about <paramref name="id"/> in its
        /// manifest. Does not imply the bytes are currently resident.
        /// </summary>
        bool Contains(BlobId id);

        /// <summary>
        /// Begins async load of <paramref name="id"/> if the store supports it.
        /// In-memory stores ignore this. Poll <see cref="GetBlobLoadingState"/>
        /// to wait for completion before calling <see cref="TryGetBlobAndMetadata"/>
        /// without blocking.
        /// </summary>
        void WarmUpBlob(BlobId id);
        BlobLoadingState GetBlobLoadingState(BlobId id);

        bool IsReadOnly { get; }
    }
}
