using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Metadata for a single blob entry in a <see cref="BlobManifest"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="LastAccessTime"/> is a monotonic per-store counter (see
    /// <see cref="Trecs.Internal.BlobStoreCommon.NextAccessTime"/>), not a wall-clock
    /// timestamp. Higher values are more recent. Values may be persisted (e.g. by
    /// <c>BlobStoreFiles</c>) and the counter is bumped above the max-loaded value
    /// when the manifest is restored so cross-run LRU ordering is preserved.
    /// </remarks>
    [TypeId(378502946)]
    public struct BlobMetadata
    {
        public TypeId TypeId;
        public long LastAccessTime;

        /// <summary>
        /// In-memory size of the native payload, in bytes — i.e. the value reported by
        /// <see cref="NativeBlobBox.Size"/> when the blob is native. Always <c>0</c> for
        /// managed (class) blobs; the byte cost of a managed object is not knowable in C#.
        /// </summary>
        /// <remarks>
        /// This is intentionally a single, store-independent unit: every <see cref="IBlobStore"/>
        /// reports the same number for the same native payload regardless of how it stores
        /// the bytes. Stores that also care about secondary sizes (e.g. <c>BlobStoreFiles</c>
        /// tracks on-disk file size for its file-cache eviction pass) keep that bookkeeping
        /// privately and do not surface it here.
        /// </remarks>
        public long NativeBytes;

        public bool IsNative;
    }

    /// <summary>
    /// Index of all known blobs in a <see cref="IBlobStore"/>, mapping
    /// <see cref="BlobId"/> to <see cref="BlobMetadata"/>.
    /// </summary>
    [TypeId(767600239)]
    public sealed class BlobManifest
    {
        public readonly IterableDictionary<BlobId, BlobMetadata> Values = new();

        internal sealed class Serializer : ISerializer<BlobManifest>
        {
            public void Deserialize(ref BlobManifest value, ISerializationReader reader)
            {
                value ??= new();

                SerializationReaderExtensions.ReadInPlace(reader, "Values", value.Values);
            }

            public void Serialize(in BlobManifest value, ISerializationWriter writer)
            {
                writer.Write<IterableDictionary<BlobId, BlobMetadata>>("Values", value.Values);
            }
        }
    }
}
