namespace Trecs
{
    public interface IBlobPtr : IBlobAnchor
    {
        bool IsNull { get; }

        bool CanGet(BlobCache blobCache);

        void WarmUp(BlobCache blobCache);
        BlobLoadingState GetLoadingState(BlobCache blobCache);
    }
}
