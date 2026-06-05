using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Per-resident-blob metadata held by the <see cref="BlobCache"/>: type, native byte size, and
    /// the native/eager discriminants. One entry exists for each blob currently resident in memory;
    /// it is dropped when the blob is evicted.
    /// </summary>
    /// <remarks>
    /// LRU eviction order is not stored here — it lives in the cache's intrusive inactive-blob
    /// lists, fixed at the moment a blob's last handle is released (an inactive blob is never read,
    /// so individual reads do not reorder it).
    /// </remarks>
    public struct BlobMetadata
    {
        public TypeId TypeId;

        /// <summary>
        /// In-memory size of the native payload, in bytes — i.e. the value reported by
        /// <see cref="NativeBlobBox.Size"/> when the blob is native. Always <c>0</c> for
        /// managed (class) blobs; the byte cost of a managed object is not knowable in C#.
        /// </summary>
        /// <remarks>
        /// This is a single, source-independent unit: the same native payload reports the same
        /// number regardless of how its bytes were produced.
        /// </remarks>
        public long NativeBytes;

        public bool IsNative;

        /// <summary>
        /// True for an <i>eager</i> blob — one whose bytes exist only in memory with no registered
        /// <see cref="Trecs.Internal.IBlobSource"/> on the <see cref="BlobFactory"/> to rebuild them
        /// (input-pipeline payloads, <c>BlobBuilder</c> output). Used by the snapshot path to
        /// identify the opaque blobs whose bytes must be persisted (a sourced blob is re-derivable
        /// and so isn't). Eviction forgets an eager blob entirely; a sourced blob is transparently
        /// re-materialized by the factory on next access.
        /// </summary>
        public bool IsEager;
    }
}
