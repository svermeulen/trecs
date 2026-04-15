using System;
using Trecs.Collections;

namespace Trecs
{
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
