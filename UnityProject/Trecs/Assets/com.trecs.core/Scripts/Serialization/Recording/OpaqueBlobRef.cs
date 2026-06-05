namespace Trecs.Internal
{
    /// <summary>
    /// One opaque (eager) blob reference read from a snapshot's OpaqueBlobs section: everything
    /// needed to restore the blob's bytes into the <see cref="BlobCache"/> from external storage
    /// (see <see cref="OpaqueBlobPersistence.Restore"/>). Obtained via
    /// <see cref="SnapshotSerializer.PeekOpaqueBlobRefs(IReadOnlySerializationData, System.Collections.Generic.List{OpaqueBlobRef})"/>
    /// before loading a snapshot whose blobs may not be resident.
    /// </summary>
    public readonly struct OpaqueBlobRef
    {
        public readonly BlobId Id;
        public readonly TypeId TypeId;
        public readonly bool IsNative;

        public OpaqueBlobRef(BlobId id, TypeId typeId, bool isNative)
        {
            Id = id;
            TypeId = typeId;
            IsNative = isNative;
        }
    }
}
