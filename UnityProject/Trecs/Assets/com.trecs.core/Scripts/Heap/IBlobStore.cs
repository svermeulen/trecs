using System;
using Trecs.Collections;

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
