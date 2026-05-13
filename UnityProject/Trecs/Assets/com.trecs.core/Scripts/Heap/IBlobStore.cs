using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Storage backend for blob data (e.g. disk, asset bundles, in-memory). One or more
    /// blob stores are composed into a <see cref="BlobCache"/>. Register custom stores via
    /// <see cref="WorldBuilder.AddBlobStore"/>. The first writable store receives new blobs.
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
        Trecs.Internal.TrecsLog Log { set; }

        /// <summary>
        /// Pool used to rent <see cref="NativeBlobBox"/> wrappers when reading native blobs
        /// off the underlying medium. <see cref="BlobCache"/> pushes the world-scoped pool
        /// here at construction time. Stores that never deserialize native blobs (e.g. the
        /// in-memory store) may ignore the setter.
        /// </summary>
        NativeBlobBoxPool NativeBlobBoxPool { set; }

        void CleanCache(DenseHashSet<BlobId> activeBlobs);

        void CreateBlobImpl(BlobId id, object blob, bool isNative);

        void ForcePurgeBlob(BlobId id);

        bool TryGetBlobAndMetadata(
            BlobId id,
            out object blob,
            out BlobMetadata metadata,
            bool updateAccessTime
        );

        bool HasBlob(BlobId id);

        void WarmUpBlob(BlobId id);
        BlobLoadingState GetBlobLoadingState(BlobId id);

        bool IsReadOnly { get; }
    }
}
