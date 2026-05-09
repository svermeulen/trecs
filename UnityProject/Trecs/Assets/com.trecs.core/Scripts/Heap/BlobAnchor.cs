namespace Trecs
{
    /// <summary>
    /// Disposable reference to a blob allocation in the <see cref="BlobCache"/>.
    /// </summary>
    public interface IBlobAnchor
    {
        void Dispose(BlobCache blobCache);
    }

    /// <summary>
    /// Concrete <see cref="IBlobAnchor"/> backed by a <see cref="PtrHandle"/>.
    /// Disposing releases the blob cache entry.
    /// </summary>
    public sealed class BlobAnchor : IBlobAnchor
    {
        public readonly PtrHandle Handle;

        public BlobAnchor(PtrHandle handle)
        {
            Handle = handle;
        }

        public void Dispose(BlobCache blobCache)
        {
            blobCache.DisposeHandle(Handle);
        }
    }
}
