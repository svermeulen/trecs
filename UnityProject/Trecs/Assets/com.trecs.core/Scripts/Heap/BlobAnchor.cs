namespace Trecs
{
    public interface IBlobAnchor
    {
        void Dispose(BlobCache blobCache);
    }

    public class BlobAnchor : IBlobAnchor
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
