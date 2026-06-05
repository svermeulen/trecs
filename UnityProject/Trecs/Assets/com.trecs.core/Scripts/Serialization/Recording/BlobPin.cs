namespace Trecs.Internal
{
    /// <summary>
    /// A type-erased pin that keeps one opaque (eager) blob resident in the <see cref="BlobCache"/>
    /// for as long as an in-memory <see cref="WorldSnapshot"/> references it. Pairs the blob's
    /// <see cref="BlobId"/> (needed to flush its bytes to the <see cref="IOpaqueBlobStore"/> at save
    /// time) with the cache <see cref="PtrHandle"/> that must be disposed (via
    /// <c>BlobCache.DisposeHandle</c>) to release the pin.
    /// <para>
    /// This is the lower-level equivalent of a <see cref="SharedAnchor{T}"/> without the typed
    /// accessor: the editor recorder never reads the blob, it only needs to keep it from being
    /// evicted while a captured snapshot still references it — the live-capture path's replacement
    /// for the old capture-time disk write-through. Disposed through
    /// <c>SnapshotStore</c>'s pin-release callback on remove/trim/cap/clear, or directly by
    /// <c>TrecsRewindBuffer</c> for the desync snapshot it owns.
    /// </para>
    /// </summary>
    internal readonly struct BlobPin
    {
        public readonly BlobId Id;
        public readonly PtrHandle Handle;

        public BlobPin(BlobId id, PtrHandle handle)
        {
            Id = id;
            Handle = handle;
        }
    }
}
