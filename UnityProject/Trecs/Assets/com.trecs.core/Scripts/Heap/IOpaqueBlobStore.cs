using System;
using System.IO;

namespace Trecs
{
    /// <summary>
    /// A content-addressed byte store for <b>opaque</b> blob persistence: <c>BlobId → bytes</c>,
    /// where the id is a content hash so an entry that already exists never needs rewriting
    /// (idempotent <see cref="Write"/>). This is the seam the snapshot/recording serializer is
    /// parameterized on when it must persist opaque (eager) blobs — those whose bytes are <i>not</i>
    /// re-derivable from a descriptor or factory and therefore can't travel as a small journal entry.
    /// <para>
    /// The store is engaged only at the persistence boundary, never as a runtime <see cref="World"/>
    /// dependency: a save streams each referenced opaque blob's bytes here, and a load opens them back
    /// and seeds the cache directly (the read-side of <see cref="OpaqueBlobBaker"/>). svkj's
    /// <c>FileBlobStore</c> is the canonical filesystem-backed implementation (with recording-rooted
    /// GC); tests can supply an in-memory one.
    /// </para>
    /// <para>
    /// <b>Stream-based by design.</b> Bytes never round-trip through a managed <c>byte[]</c>: a write
    /// hands the store a callback that serializes straight into a destination <see cref="Stream"/> (a
    /// native blob streams directly out of its <c>NativeBlobBox</c>), and a read opens a
    /// <see cref="Stream"/> the caller deserializes straight from. The store owns the durability
    /// mechanics — a <see cref="Write"/> must be atomic (stage and atomically swap into place) so a
    /// crash mid-write can't leave a truncated entry under a content id.
    /// </para>
    /// </summary>
    public interface IOpaqueBlobStore
    {
        /// <summary>True if bytes for <paramref name="id"/> are already stored.</summary>
        bool Contains(BlobId id);

        /// <summary>
        /// Stores the bytes for <paramref name="id"/> by invoking <paramref name="writeContents"/>
        /// with a writable destination <see cref="Stream"/> the implementation owns. The write must
        /// be atomic — staged and swapped into place — so an interrupted write never leaves a
        /// truncated entry. Because ids are content hashes, callers skip this when
        /// <see cref="Contains"/> is already true; an implementation may also no-op or overwrite an
        /// existing id (the bytes are identical either way).
        /// </summary>
        void Write(BlobId id, Action<Stream> writeContents);

        /// <summary>
        /// Opens a readable <see cref="Stream"/> over the stored bytes for <paramref name="id"/>,
        /// returning false if none are stored. The caller owns the returned stream and must dispose
        /// it. The stream is positioned at the start and is seekable.
        /// </summary>
        bool TryOpenRead(BlobId id, out Stream stream);
    }
}
